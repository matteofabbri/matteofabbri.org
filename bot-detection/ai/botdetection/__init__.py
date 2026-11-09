"""Bot detection inference service.

The package is deliberately small and layered:

    config      environment and structured logging
    features    the single, versioned feature schema (train == serve)
    model       model bundle, decision policy, heuristic fallback
    predictor   lock-protected snapshot of the current bundle
    hot_reload  file watcher that swaps the snapshot
    storage     SQLite dataset and training history
    messaging   RabbitMQ topology, consumer and feedback publisher
    service     gRPC servicer and metrics
    server      composition root and lifecycle
    retrain     offline/continuous training with a promotion gate
"""

__version__ = "1.0.0"
