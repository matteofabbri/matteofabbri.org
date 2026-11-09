#!/usr/bin/env bash
# Regenerate the Python stubs from the shared contract.
# The Docker build does the same, so this is only needed for local development.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/ai/proto_gen"

mkdir -p "$OUT"
python -m grpc_tools.protoc \
  -I "$ROOT/contracts" \
  --python_out="$OUT" \
  --grpc_python_out="$OUT" \
  "$ROOT/contracts/bot_detection.proto"

echo "generated: $OUT"
