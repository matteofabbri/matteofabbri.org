# syntax=docker/dockerfile:1.7
# Build context: repository root (bot-detection/). The image needs ai/ for the
# code and contracts/bot_detection.proto for code generation, so the source of
# truth stays shared with the .NET project.
FROM python:3.12-slim AS builder

ENV PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /build

COPY ai/requirements.txt ./requirements.txt
RUN python -m venv /opt/venv \
    && /opt/venv/bin/pip install --upgrade pip \
    && /opt/venv/bin/pip install -r requirements.txt

COPY contracts/bot_detection.proto ./contracts/bot_detection.proto
RUN mkdir -p /build/proto_gen \
    && /opt/venv/bin/python -m grpc_tools.protoc \
        -I /build/contracts \
        --python_out=/build/proto_gen \
        --grpc_python_out=/build/proto_gen \
        /build/contracts/bot_detection.proto

FROM python:3.12-slim AS runtime

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PATH="/opt/venv/bin:$PATH" \
    PYTHONPATH=/app/proto_gen:/app

RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin app

WORKDIR /app
COPY --from=builder /opt/venv /opt/venv
COPY --from=builder /build/proto_gen /app/proto_gen
COPY --chown=app:app ai/botdetection /app/botdetection

RUN mkdir -p /models /data && chown -R app:app /models /data
USER app

EXPOSE 50051 8081

HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
    CMD ["python", "-c", "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8081/healthz', timeout=2).status == 200 else 1)"]

ENTRYPOINT ["python", "-m", "botdetection.server"]
