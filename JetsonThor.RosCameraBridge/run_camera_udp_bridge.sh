#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

IMAGE_NAME_INPUT="${IMAGE_NAME:-GUI_camera_bridge}"
IMAGE_NAME="${IMAGE_NAME_INPUT,,}"
CONTAINER_NAME="${CONTAINER_NAME:-gui_camera_bridge}"
BASE_IMAGE="${BASE_IMAGE:-ros:jazzy-ros-base}"
WORKSPACE_DIR="${WORKSPACE_DIR:-/home/lig/gui_camera_ws}"
WORKSPACE_SETUP="${WORKSPACE_SETUP:-/ros2_ws/install/local_setup.bash}"
WORKSPACE_MOUNT_MODE="${WORKSPACE_MOUNT_MODE:-ro}"
GUI_CONFIG_FILE="${GUI_CONFIG_FILE:-$SCRIPT_DIR/../BroadcastControl.App/LigDnaGui.config.json}"

read_gui_host_from_config() {
  local config_file="$1"
  if [[ ! -f "$config_file" ]]; then
    return 1
  fi

  if command -v python3 >/dev/null 2>&1; then
    python3 - "$config_file" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], "r", encoding="utf-8") as stream:
        value = json.load(stream).get("PcGuiHost", "")
except Exception:
    value = ""

if isinstance(value, str) and value.strip():
    print(value.strip())
PY
    return
  fi

  sed -n 's/.*"PcGuiHost"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$config_file" | head -n 1
}

CONFIG_GUI_HOST="$(read_gui_host_from_config "$GUI_CONFIG_FILE" || true)"
GUI_HOST="${GUI_HOST:-${CONFIG_GUI_HOST:-192.168.1.94}}"
EO_GUI_PORT="${EO_GUI_PORT:-6000}"
IR_GUI_PORT="${IR_GUI_PORT:-6001}"
EO_IMAGE_TOPIC="${EO_IMAGE_TOPIC:-/camera/eo}"
IR_IMAGE_TOPIC="${IR_IMAGE_TOPIC:-/camera/ir}"
EO_DETECTION_TOPIC="${EO_DETECTION_TOPIC:-/tracks/eo}"
IR_DETECTION_TOPIC="${IR_DETECTION_TOPIC:-/tracks/ir}"
MOTOR_CONTROL_PORT="${MOTOR_CONTROL_PORT:-8000}"
MOTOR_ANGLE_SET_TOPIC="${MOTOR_ANGLE_SET_TOPIC:-/motor/angle/set}"
MOTOR_ANGLE_BRIDGE_ENABLED="${MOTOR_ANGLE_BRIDGE_ENABLED:-false}"
STREAM_WIDTH="${STREAM_WIDTH:-0}"
STREAM_HEIGHT="${STREAM_HEIGHT:-0}"
JPEG_QUALITY="${JPEG_QUALITY:-85}"
MAX_UDP_PAYLOAD="${MAX_UDP_PAYLOAD:-60000}"
UDP_SEND_BUFFER_BYTES="${UDP_SEND_BUFFER_BYTES:-4194304}"
SEND_STATUS_WITH_IMAGE="${SEND_STATUS_WITH_IMAGE:-true}"
JETSON_RECORDING_DIR="${JETSON_RECORDING_DIR:-/home/lig/Desktop/video}"
RECORDING_DIR="${RECORDING_DIR:-/recordings}"
RECORDING_ENABLED="${RECORDING_ENABLED:-true}"
RECORDING_SEGMENT_SECONDS="${RECORDING_SEGMENT_SECONDS:-60}"
RECORDING_FPS="${RECORDING_FPS:-15}"
RECORDING_HTTP_ENABLED="${RECORDING_HTTP_ENABLED:-true}"
RECORDING_HTTP_PORT="${RECORDING_HTTP_PORT:-8090}"
TRACKING_RECORDING_ENABLED="${TRACKING_RECORDING_ENABLED:-true}"
TRACKING_RECORDING_CONTROL_PORT="${TRACKING_RECORDING_CONTROL_PORT:-8010}"
TRACKING_RECORDING_DIR="${TRACKING_RECORDING_DIR:-$RECORDING_DIR/Tracked}"
ROS_DOMAIN_ID_VALUE="${ROS_DOMAIN_ID:-}"
RMW_IMPLEMENTATION_VALUE="${RMW_IMPLEMENTATION:-}"
FASTDDS_NO_SHM="${FASTDDS_NO_SHM:-true}"
FASTDDS_PROFILE_FILE="${FASTDDS_PROFILE_FILE:-$SCRIPT_DIR/fastdds_no_shm.xml}"
FASTDDS_CONTAINER_PROFILE_FILE="/fastdds_no_shm.xml"

BUILD_IMAGE=0

print_usage() {
  cat <<'EOF'
Usage:
  bash ./run_camera_udp_bridge.sh [--build]

This runner uses a dedicated Docker image and does not depend on minji containers.

Important environment overrides:
  IMAGE_NAME, CONTAINER_NAME, BASE_IMAGE
  WORKSPACE_DIR, WORKSPACE_SETUP, WORKSPACE_MOUNT_MODE
  GUI_CONFIG_FILE
  GUI_HOST, EO_GUI_PORT, IR_GUI_PORT
  EO_IMAGE_TOPIC, IR_IMAGE_TOPIC
  EO_DETECTION_TOPIC, IR_DETECTION_TOPIC
  MOTOR_CONTROL_PORT, MOTOR_ANGLE_SET_TOPIC, MOTOR_ANGLE_BRIDGE_ENABLED
  STREAM_WIDTH, STREAM_HEIGHT, JPEG_QUALITY, MAX_UDP_PAYLOAD
  UDP_SEND_BUFFER_BYTES, SEND_STATUS_WITH_IMAGE
  JETSON_RECORDING_DIR, RECORDING_ENABLED, RECORDING_SEGMENT_SECONDS
  RECORDING_FPS, RECORDING_HTTP_ENABLED, RECORDING_HTTP_PORT
  TRACKING_RECORDING_ENABLED, TRACKING_RECORDING_CONTROL_PORT, TRACKING_RECORDING_DIR
  ROS_DOMAIN_ID, RMW_IMPLEMENTATION
  FASTDDS_NO_SHM, FASTDDS_PROFILE_FILE
EOF
}

ensure_fastdds_no_shm_profile() {
  mkdir -p "$(dirname "$FASTDDS_PROFILE_FILE")"
  cat > "$FASTDDS_PROFILE_FILE" <<'EOF'
<?xml version="1.0" encoding="UTF-8" ?>
<profiles xmlns="http://www.eprosima.com/XMLSchemas/fastRTPS_Profiles">
  <transport_descriptors>
    <transport_descriptor>
      <transport_id>udp_transport</transport_id>
      <type>UDPv4</type>
    </transport_descriptor>
  </transport_descriptors>

  <participant profile_name="no_shm_participant" is_default_profile="true">
    <rtps>
      <userTransports>
        <transport_id>udp_transport</transport_id>
      </userTransports>
      <useBuiltinTransports>false</useBuiltinTransports>
    </rtps>
  </participant>
</profiles>
EOF
}

for arg in "$@"; do
  case "$arg" in
    --build)
      BUILD_IMAGE=1
      ;;
    --help|-h)
      print_usage
      exit 0
      ;;
    *)
      echo "Unknown option: $arg" >&2
      print_usage
      exit 1
      ;;
  esac
done

if ! sudo docker image inspect "$IMAGE_NAME" >/dev/null 2>&1; then
  BUILD_IMAGE=1
fi

if [[ "$BUILD_IMAGE" -eq 1 ]]; then
  echo "Building Docker image: $IMAGE_NAME"
  sudo docker build \
    --build-arg "BASE_IMAGE=$BASE_IMAGE" \
    -f "$SCRIPT_DIR/Dockerfile" \
    -t "$IMAGE_NAME" \
    "$SCRIPT_DIR"
fi

echo "Running camera UDP bridge container"
if [[ "$IMAGE_NAME_INPUT" != "$IMAGE_NAME" ]]; then
  echo "Docker image tags must be lowercase. Using IMAGE_NAME=$IMAGE_NAME (from $IMAGE_NAME_INPUT)"
fi
echo "IMAGE_NAME=$IMAGE_NAME"
echo "CONTAINER_NAME=$CONTAINER_NAME"
echo "BASE_IMAGE=$BASE_IMAGE"
echo "WORKSPACE_DIR=$WORKSPACE_DIR"
echo "WORKSPACE_SETUP=$WORKSPACE_SETUP"
echo "GUI_CONFIG_FILE=$GUI_CONFIG_FILE"
echo "GUI_HOST=$GUI_HOST"
echo "EO_IMAGE_TOPIC=$EO_IMAGE_TOPIC EO_GUI_PORT=$EO_GUI_PORT"
echo "IR_IMAGE_TOPIC=$IR_IMAGE_TOPIC IR_GUI_PORT=$IR_GUI_PORT"
echo "EO_DETECTION_TOPIC=$EO_DETECTION_TOPIC"
echo "IR_DETECTION_TOPIC=$IR_DETECTION_TOPIC"
echo "MOTOR_CONTROL_PORT=$MOTOR_CONTROL_PORT"
echo "MOTOR_ANGLE_SET_TOPIC=$MOTOR_ANGLE_SET_TOPIC"
echo "MOTOR_ANGLE_BRIDGE_ENABLED=$MOTOR_ANGLE_BRIDGE_ENABLED"
echo "JETSON_RECORDING_DIR=$JETSON_RECORDING_DIR"
echo "RECORDING_DIR=$RECORDING_DIR"
echo "RECORDING_SEGMENT_SECONDS=$RECORDING_SEGMENT_SECONDS"
echo "RECORDING_HTTP_PORT=$RECORDING_HTTP_PORT"
echo "TRACKING_RECORDING_ENABLED=$TRACKING_RECORDING_ENABLED"
echo "TRACKING_RECORDING_CONTROL_PORT=$TRACKING_RECORDING_CONTROL_PORT"
echo "TRACKING_RECORDING_DIR=$TRACKING_RECORDING_DIR"
echo "MAX_UDP_PAYLOAD=$MAX_UDP_PAYLOAD"
echo "FASTDDS_NO_SHM=$FASTDDS_NO_SHM"
if [[ "$FASTDDS_NO_SHM" == "true" ]]; then
  echo "FASTDDS_PROFILE_FILE=$FASTDDS_PROFILE_FILE"
fi
if [[ -n "$ROS_DOMAIN_ID_VALUE" ]]; then
  echo "ROS_DOMAIN_ID=$ROS_DOMAIN_ID_VALUE"
fi
if [[ -n "$RMW_IMPLEMENTATION_VALUE" ]]; then
  echo "RMW_IMPLEMENTATION=$RMW_IMPLEMENTATION_VALUE"
fi

if sudo docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  echo "Removing previous container: $CONTAINER_NAME"
  sudo docker rm -f "$CONTAINER_NAME" >/dev/null
fi

mkdir -p "$JETSON_RECORDING_DIR"
mkdir -p "$JETSON_RECORDING_DIR/Tracked"
if [[ "$FASTDDS_NO_SHM" == "true" ]]; then
  ensure_fastdds_no_shm_profile
fi

docker_args=(
  run --rm
  --name "$CONTAINER_NAME"
  --network host
  --ipc host
  -e "GUI_HOST=$GUI_HOST"
  -e "EO_GUI_PORT=$EO_GUI_PORT"
  -e "IR_GUI_PORT=$IR_GUI_PORT"
  -e "EO_IMAGE_TOPIC=$EO_IMAGE_TOPIC"
  -e "IR_IMAGE_TOPIC=$IR_IMAGE_TOPIC"
  -e "EO_DETECTION_TOPIC=$EO_DETECTION_TOPIC"
  -e "IR_DETECTION_TOPIC=$IR_DETECTION_TOPIC"
  -e "MOTOR_CONTROL_PORT=$MOTOR_CONTROL_PORT"
  -e "MOTOR_ANGLE_SET_TOPIC=$MOTOR_ANGLE_SET_TOPIC"
  -e "MOTOR_ANGLE_BRIDGE_ENABLED=$MOTOR_ANGLE_BRIDGE_ENABLED"
  -e "STREAM_WIDTH=$STREAM_WIDTH"
  -e "STREAM_HEIGHT=$STREAM_HEIGHT"
  -e "JPEG_QUALITY=$JPEG_QUALITY"
  -e "MAX_UDP_PAYLOAD=$MAX_UDP_PAYLOAD"
  -e "UDP_SEND_BUFFER_BYTES=$UDP_SEND_BUFFER_BYTES"
  -e "SEND_STATUS_WITH_IMAGE=$SEND_STATUS_WITH_IMAGE"
  -e "RECORDING_DIR=$RECORDING_DIR"
  -e "RECORDING_ENABLED=$RECORDING_ENABLED"
  -e "RECORDING_SEGMENT_SECONDS=$RECORDING_SEGMENT_SECONDS"
  -e "RECORDING_FPS=$RECORDING_FPS"
  -e "RECORDING_HTTP_ENABLED=$RECORDING_HTTP_ENABLED"
  -e "RECORDING_HTTP_PORT=$RECORDING_HTTP_PORT"
  -e "TRACKING_RECORDING_ENABLED=$TRACKING_RECORDING_ENABLED"
  -e "TRACKING_RECORDING_CONTROL_PORT=$TRACKING_RECORDING_CONTROL_PORT"
  -e "TRACKING_RECORDING_DIR=$TRACKING_RECORDING_DIR"
  -e "WORKSPACE_SETUP=$WORKSPACE_SETUP"
  -v "$WORKSPACE_DIR:/ros2_ws:$WORKSPACE_MOUNT_MODE"
  -v "$JETSON_RECORDING_DIR:$RECORDING_DIR:rw"
)

if [[ "$FASTDDS_NO_SHM" == "true" ]]; then
  docker_args+=(
    -e "FASTRTPS_DEFAULT_PROFILES_FILE=$FASTDDS_CONTAINER_PROFILE_FILE"
    -v "$FASTDDS_PROFILE_FILE:$FASTDDS_CONTAINER_PROFILE_FILE:ro"
  )
fi

if [[ -n "$ROS_DOMAIN_ID_VALUE" ]]; then
  docker_args+=(-e "ROS_DOMAIN_ID=$ROS_DOMAIN_ID_VALUE")
fi

if [[ -n "$RMW_IMPLEMENTATION_VALUE" ]]; then
  docker_args+=(-e "RMW_IMPLEMENTATION=$RMW_IMPLEMENTATION_VALUE")
fi

sudo docker "${docker_args[@]}" "$IMAGE_NAME"
