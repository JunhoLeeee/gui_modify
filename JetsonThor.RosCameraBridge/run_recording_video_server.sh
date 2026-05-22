#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

RECORDING_DIR="${RECORDING_DIR:-/home/lig/Desktop/video}"
RECORDING_HTTP_PORT="${RECORDING_HTTP_PORT:-8090}"

mkdir -p "$RECORDING_DIR"

echo "Serving recorded videos"
echo "RECORDING_DIR=$RECORDING_DIR"
echo "RECORDING_HTTP_PORT=$RECORDING_HTTP_PORT"

RECORDING_DIR="$RECORDING_DIR" \
RECORDING_HTTP_PORT="$RECORDING_HTTP_PORT" \
python3 "$SCRIPT_DIR/app/recording_video_server.py"
