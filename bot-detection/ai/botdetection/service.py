"""gRPC servicer, health service and Prometheus instrumentation.

The service classifies observations and appends them to the local dataset. It
never decides whether a request is allowed: the only outputs are the two flags
and a probability.
"""

from __future__ import annotations

import logging
import sqlite3
import time
from concurrent import futures
from datetime import datetime, timezone
from typing import Callable, Sequence

import grpc
from grpc_health.v1 import health, health_pb2, health_pb2_grpc
from grpc_reflection.v1alpha import reflection
from prometheus_client import Counter, Histogram

import bot_detection_pb2 as pb
import bot_detection_pb2_grpc as pb_grpc

from .config import Settings
from .features import FEATURE_SCHEMA_VERSION, Observation, to_vector
from .model import ModelBundle, decide
from .predictor import Predictor
from .storage import ObservationRecord, SqliteStore

_LOGGER = logging.getLogger(__name__)

SERVICE_NAME = "botdetection.v1.BotDetectionService"

CLASSIFY_COUNTER = Counter(
    "botdetection_classify_total",
    "Classified requests by flag.",
    ["is_bot", "is_malicious"],
)

INFERENCE_LATENCY = Histogram(
    "botdetection_inference_seconds",
    "Feature extraction plus model inference, server side.",
    buckets=(0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1),
)

RPC_LATENCY = Histogram(
    "botdetection_rpc_seconds",
    "Full gRPC handler latency by method.",
    ["method"],
    buckets=(0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1),
)


class ClientLabels:
    """Manual client labels, served to the classification workers.

    The admin page writes these at human speed, so copy-on-write with a single
    reference swap is enough: a reader sees the old map or the new one, never a
    map being mutated. No lock on the classification path.
    """

    def __init__(self, initial: dict[str, tuple[bool, bool]] | None = None) -> None:
        self._labels: dict[str, tuple[bool, bool]] = dict(initial or {})

    def get(self, client_id: str) -> tuple[bool, bool] | None:
        return self._labels.get(client_id) if client_id else None

    def set(self, client_id: str, is_bot: bool, is_malicious: bool) -> None:
        updated = dict(self._labels)
        updated[client_id] = (is_bot, is_malicious)
        self._labels = updated

    def clear(self, client_id: str) -> None:
        if client_id not in self._labels:
            return
        updated = dict(self._labels)
        updated.pop(client_id, None)
        self._labels = updated


class BotDetectionServicer(pb_grpc.BotDetectionServiceServicer):
    def __init__(
        self,
        predictor: Predictor,
        settings: Settings,
        store: SqliteStore,
        labels: ClientLabels,
        on_ingested: Callable[[int], None] | None = None,
        on_feedback: Callable[[], None] | None = None,
    ) -> None:
        self._predictor = predictor
        self._settings = settings
        self._store = store
        self._labels = labels
        self._on_ingested = on_ingested or (lambda _inserted: None)
        self._on_feedback = on_feedback

    def Classify(self, request: pb.BotClassificationRequest, context: grpc.ServicerContext):
        bundle = self._predictor.acquire()
        try:
            response, record = self._classify_one(request, bundle)
        except Exception:  # noqa: BLE001 - never leak a stack trace on the wire
            _LOGGER.exception("inference failed", extra={"request_id": request.request_id})
            context.abort(grpc.StatusCode.INTERNAL, "classification failed")
            return pb.BotClassificationResponse()

        self._record([record])
        return response

    def ClassifyBatch(
        self,
        request: pb.BotClassificationBatchRequest,
        context: grpc.ServicerContext,
    ):
        bundle = self._predictor.acquire()
        responses: list[pb.BotClassificationResponse] = []
        records: list[ObservationRecord] = []

        try:
            for item in request.requests:
                response, record = self._classify_one(item, bundle)
                responses.append(response)
                records.append(record)
        except Exception:  # noqa: BLE001
            _LOGGER.exception("batch inference failed")
            context.abort(grpc.StatusCode.INTERNAL, "classification failed")
            return pb.BotClassificationBatchResponse()

        self._record(records)
        return pb.BotClassificationBatchResponse(responses=responses)

    def GetModelInfo(self, request: pb.ModelInfoRequest, context: grpc.ServicerContext):
        bundle = self._predictor.acquire()
        return pb.ModelInfoResponse(
            model_version=bundle.version,
            trained_at=bundle.trained_at,
            feature_schema_version=bundle.schema_version,
            training_samples=bundle.training_samples,
            checksum=bundle.checksum,
            loaded_at_unix=bundle.loaded_at,
            reload_count=self._predictor.reload_count,
        )

    def LabelClient(self, request: pb.LabelClientRequest, context: grpc.ServicerContext):
        """Manual verdict for one client: dataset labels + the live override.

        The site calls this when an admin clicks a flag on a request. Both
        booleans unset means "clear the manual label": the observations go back
        to their weak labels and the next requests are the model's again.
        """
        client_id = (request.client_id or "").strip().lower()
        if not client_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "client_id is required")

        active = request.HasField("is_bot") or request.HasField("is_malicious")
        try:
            if active:
                is_bot = request.is_bot if request.HasField("is_bot") else False
                is_malicious = request.is_malicious if request.HasField("is_malicious") else False
                updated = self._store.set_client_label(
                    client_id, is_bot, is_malicious, request.labelled_by or ""
                )
                self._labels.set(client_id, is_bot, is_malicious)
            else:
                updated = self._store.clear_client_label(client_id)
                self._labels.clear(client_id)
        except sqlite3.Error:
            _LOGGER.exception("could not store the client label")
            context.abort(grpc.StatusCode.INTERNAL, "label not stored")
            return pb.LabelClientResponse()

        retrain = bool(updated and self._on_feedback is not None)
        if retrain:
            self._on_feedback()

        _LOGGER.info(
            "client label stored",
            extra={"client_id": client_id, "samples": updated},
        )
        return pb.LabelClientResponse(
            observations_updated=updated,
            manual_label_active=active,
            retrain_requested=retrain,
        )

    def _classify_one(
        self,
        item: pb.BotClassificationRequest,
        bundle: ModelBundle,
    ) -> tuple[pb.BotClassificationResponse, ObservationRecord]:
        started = time.perf_counter_ns()
        observation = Observation.from_request(item)
        probability = bundle.score(to_vector(observation))
        is_bot, is_malicious, signals = decide(probability, observation, self._settings)

        manual = self._labels.get(item.client_id)
        if manual is not None:
            # A human decided this client: the verdict wins over the model for
            # its next requests, without waiting for a retrained bundle.
            is_bot, is_malicious = manual
            signals = [*signals, "manual-client-label"]

        elapsed_seconds = (time.perf_counter_ns() - started) / 1_000_000_000
        INFERENCE_LATENCY.observe(elapsed_seconds)
        CLASSIFY_COUNTER.labels(
            "true" if is_bot else "false",
            "true" if is_malicious else "false",
        ).inc()

        response = pb.BotClassificationResponse(
            is_bot=is_bot,
            is_malicious=is_malicious,
            probability=probability,
            model_version=bundle.version,
            signals=signals,
            inference_micros=int(elapsed_seconds * 1_000_000),
            feature_schema_version=FEATURE_SCHEMA_VERSION,
        )

        record = ObservationRecord(
            request_id=item.request_id or f"{item.client_id}:{item.observed_at_unix_ms}",
            observed_at=_observed_at(item.observed_at_unix_ms),
            client_id=item.client_id,
            remote_ip=item.remote_ip,
            country_code=item.country_code,
            method=item.method or "GET",
            path=item.path or "/",
            user_agent=item.user_agent or "",
            path_has_query=item.path_has_query,
            has_referer=item.has_referer,
            has_cookies=item.has_cookies,
            is_https=item.is_https,
            is_http2=item.is_http2,
            requests_last_minute=max(item.requests_last_minute, 0),
            distinct_paths_last_minute=max(item.distinct_paths_last_minute, 0),
            errors_last_minute=max(item.errors_last_minute, 0),
            auth_failures_last_minute=max(item.auth_failures_last_minute, 0),
            seconds_since_last_request=item.seconds_since_last_request,
            utc_hour=item.utc_hour,
            http_status=item.http_status,
            duration_ms=item.duration_ms,
            is_bot=is_bot,
            is_malicious=is_malicious,
            probability=probability,
            model_version=bundle.version,
            manual_label=(1 if (is_bot or is_malicious) else 0) if manual is not None else None,
        )

        return response, record

    def _record(self, records: Sequence[ObservationRecord]) -> None:
        if not records:
            return

        try:
            inserted = self._store.insert_events(records)
        except sqlite3.Error:
            # The flags have already been computed: a dataset hiccup must not
            # fail the classification the site is waiting for.
            _LOGGER.exception("dataset write failed; the response was already computed")
            return

        if inserted:
            self._on_ingested(inserted)


class MetricsInterceptor(grpc.ServerInterceptor):
    def intercept_service(self, continuation, handler_call_details):
        handler = continuation(handler_call_details)
        if handler is None or handler.unary_unary is None:
            return handler

        method = handler_call_details.method

        def unary_unary(request, context):
            with RPC_LATENCY.labels(method).time():
                return handler.unary_unary(request, context)

        return grpc.unary_unary_rpc_method_handler(
            unary_unary,
            request_deserializer=handler.request_deserializer,
            response_serializer=handler.response_serializer,
        )


class ExceptionInterceptor(grpc.ServerInterceptor):
    """Converts an unexpected exception into INTERNAL, without details."""

    def intercept_service(self, continuation, handler_call_details):
        handler = continuation(handler_call_details)
        if handler is None or handler.unary_unary is None:
            return handler

        method = handler_call_details.method

        def unary_unary(request, context):
            try:
                return handler.unary_unary(request, context)
            except Exception:  # noqa: BLE001
                _LOGGER.exception("unhandled error in %s", method)
                context.abort(grpc.StatusCode.INTERNAL, "internal error")

        return grpc.unary_unary_rpc_method_handler(
            unary_unary,
            request_deserializer=handler.request_deserializer,
            response_serializer=handler.response_serializer,
        )


def build_grpc_server(
    settings: Settings,
    predictor: Predictor,
    store: SqliteStore,
    labels: ClientLabels,
    on_ingested: Callable[[int], None] | None = None,
    on_feedback: Callable[[], None] | None = None,
) -> grpc.Server:
    server = grpc.server(
        futures.ThreadPoolExecutor(
            max_workers=settings.grpc_workers,
            thread_name_prefix="inference",
        ),
        options=(
            ("grpc.max_receive_message_length", 4 * 1024 * 1024),
            ("grpc.max_send_message_length", 4 * 1024 * 1024),
            ("grpc.keepalive_time_ms", 30_000),
            ("grpc.keepalive_timeout_ms", 10_000),
            ("grpc.http2.min_ping_interval_without_data_ms", 10_000),
            ("grpc.so_reuseport", 0),
        ),
        interceptors=(MetricsInterceptor(), ExceptionInterceptor()),
        maximum_concurrent_rpcs=settings.max_concurrent_rpcs,
    )

    pb_grpc.add_BotDetectionServiceServicer_to_server(
        BotDetectionServicer(predictor, settings, store, labels, on_ingested, on_feedback),
        server,
    )

    health_servicer = health.HealthServicer()
    health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)
    health_servicer.set(SERVICE_NAME, health_pb2.HealthCheckResponse.SERVING)
    health_pb2_grpc.add_HealthServicer_to_server(health_servicer, server)

    if settings.enable_reflection:
        reflection.enable_server_reflection(
            (SERVICE_NAME, health.SERVICE_NAME, reflection.SERVICE_NAME),
            server,
        )

    bound_port = server.add_insecure_port(f"0.0.0.0:{settings.grpc_port}")
    if bound_port == 0:
        raise RuntimeError(f"could not bind gRPC port {settings.grpc_port}")

    return server


def _observed_at(unix_ms: int) -> str:
    if unix_ms <= 0:
        moment = datetime.now(timezone.utc)
    else:
        moment = datetime.fromtimestamp(unix_ms / 1000.0, tz=timezone.utc)
    return moment.isoformat(timespec="milliseconds")
