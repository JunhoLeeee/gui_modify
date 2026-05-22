#!/usr/bin/env python3
"""Jetson ROS2 영상/탐지 토픽을 PC GUI가 이해하는 UDP 패킷으로 변환하는 bridge."""

from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from datetime import datetime
from functools import partial
import html
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import struct
import threading
import time
from urllib.parse import quote, urlparse

import cv2
import numpy as np
import rclpy
from rclpy.node import Node
from rclpy.qos import (
    DurabilityPolicy,
    HistoryPolicy,
    QoSProfile,
    ReliabilityPolicy,
    qos_profile_sensor_data,
)
from sensor_msgs.msg import Image
try:
    from sentinel_interfaces.msg import TrackedDetection2DArray as TrackArrayMessage
except ImportError:
    from sentinel_interfaces.msg import Detection2DArray as TrackArrayMessage
try:
    from sentinel_interfaces.msg import MotorAngle
except ImportError:
    MotorAngle = None


LEGACY_IMAGE_HEADER_SIZE = 20
SENTINEL_PACKET_MAGIC = b"SNTL"
SENTINEL_IMAGE_HEADER_FORMAT = "<4sBIHHH"
SENTINEL_IMAGE_HEADER_SIZE = struct.calcsize(SENTINEL_IMAGE_HEADER_FORMAT)
SENTINEL_DETECTION_HEADER_FORMAT = "<4sBIII"
SENTINEL_DETECTION_HEADER_SIZE = struct.calcsize(SENTINEL_DETECTION_HEADER_FORMAT)
SENTINEL_DETECTION_RECORD_SIZE = 36
SENTINEL_TRACK_RECORD_SIZE = 44
IMAGE_FRAGMENT_MAGIC = b"IMGF"
IMAGE_FRAGMENT_HEADER_FORMAT = "!4sQIIHHHH"
IMAGE_FRAGMENT_HEADER_SIZE = struct.calcsize(IMAGE_FRAGMENT_HEADER_FORMAT)
DETECTION_PACKET_MAGIC = b"DETS"
STATUS_PACKET_MAGIC = b"STAT"
STAMP_HISTORY_LIMIT = 120
VIDEO_FILE_EXTENSIONS = {".mp4", ".avi", ".mov", ".mkv", ".m4v"}


def getenv_int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


def getenv_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


# 아래 환경변수들은 run_camera_udp_bridge.sh에서 주로 주입된다.
# 현장 네트워크가 바뀌면 코드 수정 없이 GUI_HOST와 포트만 바꿔 실행할 수 있게 했다.
GUI_HOST = os.getenv("GUI_HOST", "192.168.1.94")
EO_GUI_PORT = getenv_int("EO_GUI_PORT", 6000)
IR_GUI_PORT = getenv_int("IR_GUI_PORT", 6001)
EO_IMAGE_TOPIC = os.getenv("EO_IMAGE_TOPIC", "/camera/eo")
IR_IMAGE_TOPIC = os.getenv("IR_IMAGE_TOPIC", "/camera/ir")
EO_DETECTION_TOPIC = os.getenv("EO_DETECTION_TOPIC", "/tracks/eo")
IR_DETECTION_TOPIC = os.getenv("IR_DETECTION_TOPIC", "/tracks/ir")
MOTOR_CONTROL_PORT = getenv_int("MOTOR_CONTROL_PORT", 8000)
MOTOR_ANGLE_SET_TOPIC = os.getenv("MOTOR_ANGLE_SET_TOPIC", "/motor/angle/set")
MOTOR_ANGLE_BRIDGE_ENABLED = getenv_bool("MOTOR_ANGLE_BRIDGE_ENABLED", False)
STREAM_WIDTH = getenv_int("STREAM_WIDTH", 0)
STREAM_HEIGHT = getenv_int("STREAM_HEIGHT", 0)
JPEG_QUALITY = getenv_int("JPEG_QUALITY", 85)
MAX_UDP_PAYLOAD = getenv_int("MAX_UDP_PAYLOAD", 60000)
UDP_SEND_BUFFER_BYTES = getenv_int("UDP_SEND_BUFFER_BYTES", 4 * 1024 * 1024)
SEND_STATUS_WITH_IMAGE = getenv_bool("SEND_STATUS_WITH_IMAGE", True)
RECORDING_ENABLED = getenv_bool("RECORDING_ENABLED", True)
RECORDING_DIR = os.getenv("RECORDING_DIR", "/recordings")
RECORDING_SEGMENT_SECONDS = getenv_int("RECORDING_SEGMENT_SECONDS", 60)
RECORDING_FPS = getenv_int("RECORDING_FPS", 15)
RECORDING_HTTP_ENABLED = getenv_bool("RECORDING_HTTP_ENABLED", True)
RECORDING_HTTP_PORT = getenv_int("RECORDING_HTTP_PORT", 8090)
TRACKING_RECORDING_ENABLED = getenv_bool("TRACKING_RECORDING_ENABLED", RECORDING_ENABLED)
TRACKING_RECORDING_CONTROL_PORT = getenv_int("TRACKING_RECORDING_CONTROL_PORT", 8010)
TRACKING_RECORDING_DIR = os.getenv("TRACKING_RECORDING_DIR", str(Path(RECORDING_DIR) / "Tracked"))
LATEST_RECORDING_SEGMENT_NAME = ""
LATEST_RECORDING_SEGMENT_LOCK = threading.Lock()

IMAGE_TOPIC_QOS = QoSProfile(
    history=HistoryPolicy.KEEP_LAST,
    depth=5,
    reliability=ReliabilityPolicy.RELIABLE,
    durability=DurabilityPolicy.VOLATILE,
)


def current_recording_segment_name() -> str:
    now = datetime.now().replace(second=0, microsecond=0)
    return now.strftime("%Y%m%d_%H%M%S")


def safe_segment_name(value: object | None) -> str:
    if not isinstance(value, str) or not value.strip():
        return current_recording_segment_name()

    cleaned = "".join(ch for ch in value.strip() if ch.isalnum() or ch in {"_", "-"})
    return cleaned or current_recording_segment_name()


def latest_recording_segment_name(directory: Path) -> str:
    if LATEST_RECORDING_SEGMENT_NAME:
        return LATEST_RECORDING_SEGMENT_NAME

    existing_folders = sorted(
        [item for item in directory.iterdir() if item.is_dir()],
        key=lambda item: item.stat().st_mtime,
        reverse=True,
    )
    if existing_folders:
        return safe_segment_name(existing_folders[0].name)

    return current_recording_segment_name()


def write_text_file(directory: Path, name: str, content: object | None) -> Path | None:
    if not isinstance(content, str):
        return None

    directory.mkdir(parents=True, exist_ok=True)
    path = directory / name
    path.write_text(content, encoding="utf-8")
    return path


def recording_video_path(directory: Path, timestamp: str, stream_name: str) -> Path:
    return directory / timestamp / f"{stream_name.upper()}_{timestamp}.mp4"


def default_system_log_text(timestamp: str) -> str:
    return (
        "LIG DNA GUI System Log\n"
        f"Saved At: {datetime.now():%Y-%m-%d %H:%M:%S}\n"
        f"Recording Folder: {timestamp}\n\n"
        "No GUI system log was received for this recording segment yet.\n"
    )


def default_vlm_analysis_text(timestamp: str) -> str:
    return (
        "LIG DNA GUI VLM Analysis Result\n"
        f"Saved At: {datetime.now():%Y-%m-%d %H:%M:%S}\n"
        f"Recording Folder: {timestamp}\n\n"
        "No VLM analysis result was received for this recording segment yet.\n"
    )


def ensure_default_metadata_files(segment_directory: Path, timestamp: str) -> None:
    segment_directory.mkdir(parents=True, exist_ok=True)
    system_path = segment_directory / f"system_log_{timestamp}.txt"
    analysis_path = segment_directory / f"vlm_analysis_{timestamp}.txt"

    if not system_path.exists():
        system_path.write_text(default_system_log_text(timestamp), encoding="utf-8")

    if not analysis_path.exists():
        analysis_path.write_text(default_vlm_analysis_text(timestamp), encoding="utf-8")


def write_placeholder_video(path: Path, stream_name: str) -> None:
    if path.exists():
        return

    path.parent.mkdir(parents=True, exist_ok=True)
    width = max(320, STREAM_WIDTH)
    height = max(180, STREAM_HEIGHT)
    fourcc = cv2.VideoWriter_fourcc(*"mp4v")
    writer = cv2.VideoWriter(str(path), fourcc, max(1, RECORDING_FPS), (width, height))
    if not writer.isOpened():
        return

    try:
        frame = np.zeros((height, width, 3), dtype=np.uint8)
        cv2.putText(
            frame,
            f"{stream_name.upper()} NO SIGNAL",
            (32, height // 2),
            cv2.FONT_HERSHEY_SIMPLEX,
            1.0,
            (180, 180, 180),
            2,
            cv2.LINE_AA,
        )
        cv2.putText(
            frame,
            datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            (32, min(height - 28, height // 2 + 44)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.65,
            (120, 120, 120),
            1,
            cv2.LINE_AA,
        )
        for _ in range(max(1, RECORDING_FPS)):
            writer.write(frame)
    finally:
        writer.release()


def ensure_recording_segment(directory: Path, timestamp: str | None = None, create_placeholder_videos: bool = True) -> str:
    global LATEST_RECORDING_SEGMENT_NAME

    # 녹화 폴더는 1분 단위 timestamp로 만들고, 영상이 아직 안 들어와도 로그/더미 파일을 먼저 생성한다.
    # GUI에서 녹화 목록을 열었을 때 폴더 구조가 항상 보이도록 하기 위한 처리다.
    timestamp = safe_segment_name(timestamp) if timestamp else current_recording_segment_name()
    with LATEST_RECORDING_SEGMENT_LOCK:
        segment_directory = directory / timestamp
        ensure_default_metadata_files(segment_directory, timestamp)
        if create_placeholder_videos:
            write_placeholder_video(recording_video_path(directory, timestamp, "eo"), "eo")
            write_placeholder_video(recording_video_path(directory, timestamp, "ir_color"), "ir_color")
            write_placeholder_video(recording_video_path(directory, timestamp, "ir_gray"), "ir_gray")

        LATEST_RECORDING_SEGMENT_NAME = timestamp
        return timestamp


def create_ir_false_color_frame(frame: np.ndarray) -> np.ndarray:
    if frame.ndim == 3 and frame.shape[2] == 3:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    elif frame.ndim == 3 and frame.shape[2] == 4:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGRA2GRAY)
    else:
        gray = frame

    if gray.dtype != np.uint8:
        gray8 = cv2.normalize(gray, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    else:
        gray8 = gray

    return cv2.applyColorMap(gray8, cv2.COLORMAP_JET)


def create_ir_grayscale_frame(frame: np.ndarray) -> np.ndarray:
    if frame.ndim == 3 and frame.shape[2] == 3:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    elif frame.ndim == 3 and frame.shape[2] == 4:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGRA2GRAY)
    else:
        gray = frame

    if gray.dtype != np.uint8:
        gray8 = cv2.normalize(gray, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    else:
        gray8 = gray

    return cv2.cvtColor(gray8, cv2.COLOR_GRAY2BGR)

DETECTION_TOPIC_QOS = QoSProfile(
    history=HistoryPolicy.KEEP_LAST,
    depth=20,
    reliability=ReliabilityPolicy.RELIABLE,
    durability=DurabilityPolicy.VOLATILE,
)


def ros_image_to_bgr(message: Image) -> np.ndarray:
    # ROS2 Image는 encoding에 따라 바이트 배열 해석 방법이 다르다.
    # GUI 전송과 녹화를 단순하게 하기 위해 여기서 모두 OpenCV BGR 3채널 이미지로 통일한다.
    encoding = message.encoding.lower()
    height = int(message.height)
    width = int(message.width)
    step = int(message.step)
    data = np.frombuffer(message.data, dtype=np.uint8)

    if encoding in {"bgr8", "rgb8"}:
        channels = 3
        row = data.reshape(height, step)[:, : width * channels]
        image = row.reshape(height, width, channels)
        if encoding == "rgb8":
            image = cv2.cvtColor(image, cv2.COLOR_RGB2BGR)
        return image

    if encoding in {"bgra8", "rgba8"}:
        channels = 4
        row = data.reshape(height, step)[:, : width * channels]
        image = row.reshape(height, width, channels)
        if encoding == "rgba8":
            return cv2.cvtColor(image, cv2.COLOR_RGBA2BGR)
        return cv2.cvtColor(image, cv2.COLOR_BGRA2BGR)

    if encoding in {"mono8", "8uc1"}:
        row = data.reshape(height, step)[:, :width]
        return cv2.cvtColor(row.reshape(height, width), cv2.COLOR_GRAY2BGR)

    raise ValueError(f"Unsupported image encoding: {message.encoding}")


def build_image_packet(encoded_bytes: bytes, width: int, height: int, frame_index: int, stamp_ns: int) -> bytes:
    header = struct.pack(
        "!QIIHH",
        max(0, stamp_ns),
        max(0, frame_index),
        len(encoded_bytes),
        max(0, width),
        max(0, height),
    )
    return header + encoded_bytes


def build_image_packets(encoded_bytes: bytes, width: int, height: int, frame_index: int, stamp_ns: int, packet_type: int) -> list[bytes]:
    # 한 장의 JPEG가 UDP MTU보다 클 수 있으므로 여러 조각으로 나눈다.
    # GUI는 frame_index와 fragment_index를 보고 다시 순서대로 합친다.
    fragment_payload_size = max(1024, MAX_UDP_PAYLOAD - SENTINEL_IMAGE_HEADER_SIZE)
    fragment_count = (len(encoded_bytes) + fragment_payload_size - 1) // fragment_payload_size
    if fragment_count > 65535:
        raise RuntimeError(f"Encoded {width}x{height} frame is too large to fragment: {len(encoded_bytes)} bytes")

    packets: list[bytes] = []
    for fragment_index in range(fragment_count):
        start = fragment_index * fragment_payload_size
        end = min(start + fragment_payload_size, len(encoded_bytes))
        header = struct.pack(
            SENTINEL_IMAGE_HEADER_FORMAT,
            SENTINEL_PACKET_MAGIC,
            packet_type,
            max(0, frame_index),
            fragment_index,
            fragment_count,
            end - start,
        )
        packets.append(header + encoded_bytes[start:end])
    return packets


def fixed_utf8(value: object, length: int = 16) -> bytes:
    raw = str(value or "unknown").encode("utf-8")[:length]
    return raw + (b"\0" * (length - len(raw)))


def build_detection_packet(stamp_ns: int, frame_index: int, width: int, height: int, detections: list[dict]) -> bytes:
    # detection은 SNTL type 0x10 패킷으로 보낸다.
    # 현재 GUI는 track_id를 objectId로 사용하므로, 모터 추적용 객체 ID도 이 값과 동일하다.
    stamp_sec = max(0, stamp_ns) // 1_000_000_000
    stamp_nsec = max(0, stamp_ns) % 1_000_000_000
    packet = bytearray(
        struct.pack(
            SENTINEL_DETECTION_HEADER_FORMAT,
            SENTINEL_PACKET_MAGIC,
            0x10,
            max(0, frame_index),
            stamp_sec,
            stamp_nsec,
        )
    )
    packet += struct.pack("<H", 0)
    packet += struct.pack("<H", min(len(detections), 65535))
    for detection in detections[:65535]:
        packet += struct.pack(
            "<ii16sfffff",
            int(detection.get("objectId", -1)),
            int(detection.get("classId", -1)),
            fixed_utf8(detection.get("className")),
            float(detection.get("score", 0.0)),
            float(detection.get("x1", 0.0)),
            float(detection.get("y1", 0.0)),
            float(detection.get("x2", 0.0)),
            float(detection.get("y2", 0.0)),
        )
    return bytes(packet)


def build_status_packet(source: str, stamp_ns: int, frame_index: int, last_error: str = "") -> bytes:
    payload = {
        "enabled": True,
        "modelLoaded": True,
        "confThreshold": 0.0,
        "lastError": last_error,
        "source": source,
        "stampNs": max(0, stamp_ns),
        "frameId": max(0, frame_index),
    }
    return STATUS_PACKET_MAGIC + json.dumps(payload, separators=(",", ":")).encode("utf-8")


def fit_frame_to_stream(frame: np.ndarray) -> np.ndarray:
    if STREAM_WIDTH <= 0 or STREAM_HEIGHT <= 0:
        return frame

    height, width = frame.shape[:2]
    scale = min(STREAM_WIDTH / width, STREAM_HEIGHT / height, 1.0)
    target_width = max(2, int(width * scale))
    target_height = max(2, int(height * scale))
    if target_width == width and target_height == height:
        return frame
    return cv2.resize(frame, (target_width, target_height), interpolation=cv2.INTER_AREA)


def extract_stamp_ns(message_or_header) -> int:
    if hasattr(message_or_header, "header") and hasattr(message_or_header.header, "stamp"):
        stamp = message_or_header.header.stamp
    elif hasattr(message_or_header, "stamp"):
        stamp = message_or_header.stamp
    else:
        stamp = message_or_header
    return int(stamp.sec) * 1_000_000_000 + int(stamp.nanosec)


@dataclass(frozen=True)
class FrameStampInfo:
    stamp_ns: int
    frame_index: int
    width: int
    height: int


class VideoSegmentRecorder:
    def __init__(self, name: str, directory: str, segment_seconds: int, fps: int) -> None:
        self.name = name
        self.directory = Path(directory)
        self.segment_seconds = max(10, segment_seconds)
        self.fps = max(1, fps)
        self._lock = threading.Lock()
        self._writer: cv2.VideoWriter | None = None
        self._segment_started_at = 0.0
        self._current_segment_name = ""
        self._frame_size: tuple[int, int] | None = None
        self._current_path: Path | None = None
        self.directory.mkdir(parents=True, exist_ok=True)

    def write(self, frame: np.ndarray) -> None:
        if frame.size == 0:
            return

        height, width = frame.shape[:2]
        now = time.monotonic()
        segment_name = current_recording_segment_name()

        with self._lock:
            needs_new_segment = (
                self._writer is None
                or self._frame_size != (width, height)
                or self._current_segment_name != segment_name
                or now - self._segment_started_at >= self.segment_seconds
            )
            if needs_new_segment:
                self._start_segment(width, height, now, segment_name)

            if self._writer is not None:
                self._writer.write(frame)

    def close(self) -> None:
        with self._lock:
            if self._writer is not None:
                self._writer.release()
                self._writer = None
                self._current_segment_name = ""

    def _start_segment(self, width: int, height: int, now: float, timestamp: str) -> None:
        if self._writer is not None:
            self._writer.release()

        timestamp = ensure_recording_segment(self.directory, timestamp)
        self._current_path = recording_video_path(self.directory, timestamp, self.name)
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        writer = cv2.VideoWriter(str(self._current_path), fourcc, self.fps, (width, height))
        if not writer.isOpened():
            self._writer = None
            raise RuntimeError(f"Failed to open recording file: {self._current_path}")

        self._writer = writer
        self._segment_started_at = now
        self._current_segment_name = timestamp
        self._frame_size = (width, height)
        print(f"Recording segment started: {self._current_path}", flush=True)


class TrackingVideoRecorder:
    def __init__(self, directory: str, fps: int) -> None:
        self.directory = Path(directory)
        self.fps = max(1, fps)
        self._lock = threading.Lock()
        self._writer: cv2.VideoWriter | None = None
        self._frame_size: tuple[int, int] | None = None
        self._current_path: Path | None = None
        self._active = False
        self._source = "eo"
        self._object_id = -1
        self.directory.mkdir(parents=True, exist_ok=True)

    def set_state(self, active: bool, source: str, object_id: int) -> bool:
        source = "ir" if source.lower() == "ir" else "eo"
        active = active and object_id >= 0
        with self._lock:
            changed = (
                self._active != active
                or self._source != source
                or self._object_id != object_id
            )
            if not changed:
                return False

            if self._writer is not None:
                self._close_locked()

            self._active = active
            self._source = source
            self._object_id = object_id if active else -1
            return True

    def write(self, source: str, frame: np.ndarray) -> None:
        if frame.size == 0:
            return

        source = "ir" if source.lower() == "ir" else "eo"
        height, width = frame.shape[:2]
        with self._lock:
            if not self._active or source != self._source:
                return

            if self._writer is None or self._frame_size != (width, height):
                try:
                    self._start_locked(width, height)
                except Exception as exc:
                    print(f"Tracking recording failed to start: {exc}", flush=True)
                    return

            if self._writer is not None:
                self._writer.write(frame)

    def close(self) -> None:
        with self._lock:
            self._active = False
            self._object_id = -1
            self._close_locked()

    def _start_locked(self, width: int, height: int) -> None:
        if self._writer is not None:
            self._close_locked()

        self.directory.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        path = self.directory / f"Tracking_{timestamp}.mp4"
        suffix = 1
        while path.exists():
            path = self.directory / f"Tracking_{timestamp}_{suffix}.mp4"
            suffix += 1

        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        writer = cv2.VideoWriter(str(path), fourcc, self.fps, (width, height))
        if not writer.isOpened():
            writer.release()
            self._writer = None
            raise RuntimeError(f"Failed to open tracking recording file: {path}")

        self._writer = writer
        self._frame_size = (width, height)
        self._current_path = path
        print(
            f"Tracking recording started: {path} "
            f"(source={self._source}, object_id={self._object_id})",
            flush=True,
        )

    def _close_locked(self) -> None:
        if self._writer is not None:
            self._writer.release()
            print(f"Tracking recording saved: {self._current_path}", flush=True)
        self._writer = None
        self._frame_size = None
        self._current_path = None


class RecordingSegmentScheduler:
    def __init__(self, directory: str, segment_seconds: int) -> None:
        self.directory = Path(directory)
        self._stop_event = threading.Event()
        self._thread = threading.Thread(
            target=self._run,
            name="recording-segment-scheduler",
            daemon=True,
        )
        self.directory.mkdir(parents=True, exist_ok=True)
        timestamp = ensure_recording_segment(self.directory)
        print(f"Recording segment folder ready: {self.directory / timestamp}", flush=True)
        self._thread.start()

    def close(self) -> None:
        self._stop_event.set()
        self._thread.join(timeout=2.0)

    def _run(self) -> None:
        last_timestamp = LATEST_RECORDING_SEGMENT_NAME
        while not self._stop_event.wait(1.0):
            timestamp = current_recording_segment_name()
            if timestamp == last_timestamp:
                continue

            last_timestamp = ensure_recording_segment(self.directory, timestamp)
            print(f"Recording segment folder ready: {self.directory / last_timestamp}", flush=True)


class RecordingVideoHandler(SimpleHTTPRequestHandler):
    def log_message(self, format: str, *args) -> None:
        return

    def do_GET(self) -> None:
        parsed_path = urlparse(self.path).path
        if parsed_path == "/api/videos":
            self._send_video_list()
            return

        if parsed_path in {"/", "/index.html"}:
            self._send_player_page()
            return

        super().do_GET()

    def do_POST(self) -> None:
        parsed_path = urlparse(self.path).path
        if parsed_path == "/api/logs":
            self._save_metadata_logs()
            return

        self.send_error(404, "Not Found")

    def _send_video_list(self) -> None:
        directory = Path(self.directory)
        files = sorted(
            [
                item
                for item in directory.rglob("*")
                if item.is_file() and item.suffix.lower() in VIDEO_FILE_EXTENSIONS
            ],
            key=lambda item: item.stat().st_mtime,
            reverse=True,
        )
        payload = [
            {
                "name": item.relative_to(directory).as_posix(),
                "url": quote(item.relative_to(directory).as_posix(), safe="/"),
                "sizeBytes": item.stat().st_size,
                "modifiedUnixMs": int(item.stat().st_mtime * 1000),
            }
            for item in files
        ]
        encoded = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)

    def _send_player_page(self) -> None:
        directory = Path(self.directory)
        files = sorted(
            [
                item.relative_to(directory).as_posix()
                for item in directory.rglob("*")
                if item.is_file() and item.suffix.lower() in VIDEO_FILE_EXTENSIONS
            ],
            reverse=True,
        )
        options = "\n".join(
            f'<option value="{quote(name, safe="/")}">{html.escape(name)}</option>' for name in files
        )
        initial_source = quote(files[0], safe="/") if files else ""
        body = f"""<!doctype html>
<html lang="ko">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Recorded Videos</title>
  <style>
    body {{ margin: 0; padding: 24px; background: #111827; color: #e5e7eb; font-family: sans-serif; }}
    h1 {{ margin: 0 0 16px; font-size: 24px; }}
    .bar {{ display: flex; gap: 12px; margin-bottom: 16px; flex-wrap: wrap; }}
    select {{ min-height: 38px; padding: 6px 10px; background: #1f2937; color: #f9fafb; border: 1px solid #4b5563; }}
    video {{ width: 100%; max-height: 72vh; background: #000; }}
    .empty {{ padding: 32px; border: 1px solid #374151; background: #1f2937; }}
  </style>
</head>
<body>
  <h1>녹화 영상 보기</h1>
  <div class="bar">
    <select id="fileList">{options}</select>
    <select id="speed">
      <option value="0.25">0.25x</option>
      <option value="0.5">0.5x</option>
      <option value="0.75">0.75x</option>
      <option value="1" selected>1.0x</option>
      <option value="1.5">1.5x</option>
      <option value="2">2.0x</option>
    </select>
  </div>
  {('<video id="player" controls src="' + initial_source + '"></video>') if files else '<div class="empty">저장된 영상이 아직 없습니다.</div>'}
  <script>
    const fileList = document.getElementById('fileList');
    const speed = document.getElementById('speed');
    const player = document.getElementById('player');
    if (fileList && player) {{
      fileList.addEventListener('change', () => {{
        player.src = fileList.value;
        player.load();
      }});
      speed.addEventListener('change', () => {{
        player.playbackRate = Number(speed.value);
      }});
    }}
  </script>
</body>
</html>"""
        encoded = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def _save_metadata_logs(self) -> None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0

        try:
            raw_body = self.rfile.read(length) if length > 0 else b"{}"
            payload = json.loads(raw_body.decode("utf-8"))
        except Exception as exc:
            self.send_error(400, f"Invalid JSON: {exc}")
            return

        directory = Path(self.directory)
        requested_folder = payload.get("folderName")
        timestamp = ensure_recording_segment(directory, requested_folder)
        segment_directory = directory / timestamp
        manual = bool(payload.get("manual"))
        prefix = "C_" if manual else ""
        saved_files = []

        system_text = payload.get("systemLogText")
        if not isinstance(system_text, str) or not system_text.strip():
            system_text = default_system_log_text(timestamp)

        analysis_text = payload.get("analysisText")
        if not isinstance(analysis_text, str) or not analysis_text.strip():
            analysis_text = default_vlm_analysis_text(timestamp)

        system_path = write_text_file(
            segment_directory,
            f"{prefix}system_log_{timestamp}.txt",
            system_text,
        )
        if system_path is not None:
            saved_files.append(system_path.relative_to(directory).as_posix())

        analysis_path = write_text_file(
            segment_directory,
            f"{prefix}vlm_analysis_{timestamp}.txt",
            analysis_text,
        )
        if analysis_path is not None:
            saved_files.append(analysis_path.relative_to(directory).as_posix())

        encoded = json.dumps(
            {"folder": timestamp, "savedFiles": saved_files},
            separators=(",", ":"),
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)


class TrackingRecordingControlReceiver:
    PACKET_MAGIC = b"TRCK"
    PACKET_SIZE = 10

    def __init__(self, node: Node, port: int, recorder: TrackingVideoRecorder) -> None:
        self.node = node
        self.port = port
        self.recorder = recorder
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("0.0.0.0", port))
        self.sock.settimeout(0.5)
        self._stop_event = threading.Event()
        self.thread = threading.Thread(target=self._receive_loop, name="tracking-recording-control", daemon=True)
        self.thread.start()
        self.node.get_logger().info(f"Listening for tracking recording control on UDP port {port}")

    def _receive_loop(self) -> None:
        while not self._stop_event.is_set():
            try:
                packet, _ = self.sock.recvfrom(1024)
            except socket.timeout:
                continue
            except OSError:
                return

            if len(packet) < self.PACKET_SIZE or packet[:4] != self.PACKET_MAGIC:
                continue

            active = packet[4] != 0
            source = "eo" if packet[5] == 1 else "ir"
            object_id = struct.unpack_from("<i", packet, 6)[0]
            try:
                changed = self.recorder.set_state(active, source, object_id)
                if changed:
                    state = "started" if active and object_id >= 0 else "stopped"
                    self.node.get_logger().info(
                        f"Tracking recording {state}: source={source}, object_id={object_id}"
                    )
            except Exception as exc:
                self.node.get_logger().error(f"Failed to update tracking recording state: {exc}")

    def close(self) -> None:
        self._stop_event.set()
        self.sock.close()
        self.thread.join(timeout=1.0)


class StreamBridge:
    # EO 또는 IR 한 스트림을 담당하는 객체다.
    # ROS2 image topic을 JPEG UDP 패킷으로 바꾸고, 필요하면 같은 프레임을 녹화 파일에도 저장한다.
    def __init__(
        self,
        name: str,
        image_topic: str,
        detection_topic: str,
        host: str,
        port: int,
        tracking_recorder: TrackingVideoRecorder | None = None,
    ) -> None:
        self.name = name
        self.image_topic = image_topic
        self.detection_topic = detection_topic
        self.host = host
        self.port = port
        self._tracking_recorder = tracking_recorder
        self.frame_index = 0
        self.first_image_logged = False
        self.first_detection_logged = False
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        if UDP_SEND_BUFFER_BYTES > 0:
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, UDP_SEND_BUFFER_BYTES)

        self._lock = threading.Lock()
        self._stamp_history: deque[FrameStampInfo] = deque(maxlen=STAMP_HISTORY_LIMIT)
        self._last_frame_info = FrameStampInfo(0, 0, 0, 0)
        self._recorders: list[VideoSegmentRecorder] = []
        if RECORDING_ENABLED:
            if name.lower() == "ir":
                self._recorders = [
                    VideoSegmentRecorder("ir_color", RECORDING_DIR, RECORDING_SEGMENT_SECONDS, RECORDING_FPS),
                    VideoSegmentRecorder("ir_gray", RECORDING_DIR, RECORDING_SEGMENT_SECONDS, RECORDING_FPS),
                ]
            else:
                self._recorders = [
                    VideoSegmentRecorder(name, RECORDING_DIR, RECORDING_SEGMENT_SECONDS, RECORDING_FPS)
                ]

    def send_image(self, message: Image) -> None:
        # ROS2에서 받은 원본 프레임을 GUI 전송용 크기/포맷으로 맞춘 뒤 JPEG 청크로 전송한다.
        # stamp와 frame_index는 detection 패킷을 같은 프레임 위에 그리기 위한 기준으로 함께 보관한다.
        frame = ros_image_to_bgr(message)
        source_height, source_width = frame.shape[:2]
        stamp_ns = extract_stamp_ns(message.header)
        self.frame_index += 1
        frame_index = self.frame_index

        stream_frame = fit_frame_to_stream(frame)
        if self._recorders:
            if self.name.lower() == "ir":
                self._recorders[0].write(create_ir_false_color_frame(stream_frame))
                self._recorders[1].write(create_ir_grayscale_frame(stream_frame))
            else:
                self._recorders[0].write(stream_frame)

        if self._tracking_recorder is not None:
            tracking_frame = create_ir_false_color_frame(stream_frame) if self.name.lower() == "ir" else stream_frame
            self._tracking_recorder.write(self.name, tracking_frame)

        ok, encoded = cv2.imencode(
            ".jpg",
            stream_frame,
            [int(cv2.IMWRITE_JPEG_QUALITY), JPEG_QUALITY],
        )
        if not ok:
            raise RuntimeError(f"{self.name.upper()} JPEG encode failed")

        packets = build_image_packets(
            encoded.tobytes(),
            stream_frame.shape[1],
            stream_frame.shape[0],
            frame_index,
            stamp_ns,
            0x01 if self.name == "eo" else 0x02,
        )
        for packet in packets:
            self.sock.sendto(packet, (self.host, self.port))

        frame_info = FrameStampInfo(stamp_ns, frame_index, source_width, source_height)
        with self._lock:
            self._stamp_history.append(frame_info)
            self._last_frame_info = frame_info

        if SEND_STATUS_WITH_IMAGE:
            status_packet = build_status_packet(self.image_topic, stamp_ns, frame_index)
            self.sock.sendto(status_packet, (self.host, self.port))

    def send_detection(self, message) -> None:
        # 현재 명세에서는 EO detection만 EO 영상 포트 6000으로 함께 전송한다.
        # IR detection을 별도로 보내야 할 경우 이 early return을 제거하고 IR 포트 정책을 맞추면 된다.
        if self.name != "eo":
            return

        stamp_ns = extract_stamp_ns(message)
        with self._lock:
            frame_info = self._match_frame_info(stamp_ns)

        detections: list[dict] = []
        tracks = getattr(message, "tracks", getattr(message, "detections", []))
        for index, track in enumerate(tracks, start=1):
            x1 = float(track.x1)
            y1 = float(track.y1)
            x2 = float(track.x2)
            y2 = float(track.y2)
            if x2 <= x1 or y2 <= y1:
                continue

            detections.append(
                {
                    "className": track.class_name,
                    "classId": int(getattr(track, "class_id", -1)),
                    "score": float(track.score),
                    "x1": x1,
                    "y1": y1,
                    "x2": x2,
                    "y2": y2,
                    "objectId": int(getattr(track, "track_id", index)),
                }
            )

        packet = build_detection_packet(
            stamp_ns if stamp_ns > 0 else frame_info.stamp_ns,
            frame_info.frame_index,
            frame_info.width,
            frame_info.height,
            detections,
        )
        self.sock.sendto(packet, (self.host, self.port))

    def _match_frame_info(self, stamp_ns: int) -> FrameStampInfo:
        if stamp_ns > 0:
            for item in reversed(self._stamp_history):
                if item.stamp_ns == stamp_ns:
                    return item

        if self._last_frame_info.frame_index > 0:
            return self._last_frame_info

        return FrameStampInfo(stamp_ns, 0, 0, 0)

    def close(self) -> None:
        for recorder in self._recorders:
            recorder.close()


class MotorCommandUdpReceiver:
    COMMAND_PACKET_SIZE = 10

    def __init__(self, node: Node, port: int, topic: str) -> None:
        self.node = node
        self.port = port
        self.topic = topic
        self.publisher = None
        self.sock: socket.socket | None = None
        self.thread: threading.Thread | None = None
        self._stop_event = threading.Event()

        if MotorAngle is None:
            self.node.get_logger().warning(
                "sentinel_interfaces/msg/MotorAngle is unavailable; GUI motor UDP commands will not be published."
            )
            return

        self.publisher = self.node.create_publisher(MotorAngle, topic, 10)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("0.0.0.0", port))
        self.sock.settimeout(0.5)
        self.thread = threading.Thread(target=self._receive_loop, name="motor-command-udp", daemon=True)
        self.thread.start()
        self.node.get_logger().info(f"Listening for GUI motor commands on UDP port {port}")
        self.node.get_logger().info(f"Publishing motor angle commands to {topic}")

    def _receive_loop(self) -> None:
        assert self.sock is not None
        while not self._stop_event.is_set():
            try:
                packet, _ = self.sock.recvfrom(1024)
            except socket.timeout:
                continue
            except OSError:
                return

            if len(packet) < self.COMMAND_PACKET_SIZE:
                continue

            try:
                button_mask = packet[3]
                if button_mask != 0:
                    continue

                pan = self._clamp_raw(struct.unpack_from("<H", packet, 4)[0])
                tilt = self._clamp_raw(struct.unpack_from("<H", packet, 6)[0])
                self._publish_motor_angle(pan, tilt)
            except Exception as exc:
                self.node.get_logger().error(f"Failed to publish GUI motor command: {exc}")

    def _publish_motor_angle(self, pan: int, tilt: int) -> None:
        if self.publisher is None or MotorAngle is None:
            return

        message = MotorAngle()
        message.pan = pan
        message.tilt = tilt
        self.publisher.publish(message)

    @staticmethod
    def _clamp_raw(value: int) -> int:
        return max(0, min(4095, int(value)))

    def close(self) -> None:
        self._stop_event.set()
        if self.sock is not None:
            self.sock.close()
        if self.thread is not None:
            self.thread.join(timeout=1.0)


class CameraUdpBridge(Node):
    def __init__(self) -> None:
        super().__init__("camera_udp_bridge")
        # ROS2 topic 구독은 Jetson 컨테이너 내부에서 수행하고, PC GUI는 ROS2를 직접 보지 않는다.
        # 이 노드가 topic 데이터를 GUI 전용 UDP 프로토콜로 변환하는 경계 역할을 한다.
        self._tracking_recorder = (
            TrackingVideoRecorder(TRACKING_RECORDING_DIR, RECORDING_FPS)
            if TRACKING_RECORDING_ENABLED
            else None
        )
        self._tracking_control = (
            TrackingRecordingControlReceiver(self, TRACKING_RECORDING_CONTROL_PORT, self._tracking_recorder)
            if self._tracking_recorder is not None
            else None
        )
        self._eo = StreamBridge("eo", EO_IMAGE_TOPIC, EO_DETECTION_TOPIC, GUI_HOST, EO_GUI_PORT, self._tracking_recorder)
        self._ir = StreamBridge("ir", IR_IMAGE_TOPIC, IR_DETECTION_TOPIC, GUI_HOST, IR_GUI_PORT, self._tracking_recorder)
        self._motor = (
            MotorCommandUdpReceiver(self, MOTOR_CONTROL_PORT, MOTOR_ANGLE_SET_TOPIC)
            if MOTOR_ANGLE_BRIDGE_ENABLED
            else None
        )
        self._recording_http_server: ThreadingHTTPServer | None = None
        self._recording_http_thread: threading.Thread | None = None
        self._recording_segment_scheduler = (
            RecordingSegmentScheduler(RECORDING_DIR, RECORDING_SEGMENT_SECONDS)
            if RECORDING_ENABLED
            else None
        )

        self.create_subscription(Image, EO_IMAGE_TOPIC, self._on_eo_image, IMAGE_TOPIC_QOS)
        self.create_subscription(Image, IR_IMAGE_TOPIC, self._on_ir_image, IMAGE_TOPIC_QOS)
        self.create_subscription(TrackArrayMessage, EO_DETECTION_TOPIC, self._on_eo_detection, DETECTION_TOPIC_QOS)
        self.create_subscription(TrackArrayMessage, IR_DETECTION_TOPIC, self._on_ir_detection, DETECTION_TOPIC_QOS)

        self.get_logger().info(f"EO image topic: {EO_IMAGE_TOPIC}")
        self.get_logger().info(f"IR image topic: {IR_IMAGE_TOPIC}")
        self.get_logger().info(f"EO detection topic: {EO_DETECTION_TOPIC}")
        self.get_logger().info(f"IR detection topic: {IR_DETECTION_TOPIC}")
        self.get_logger().info(f"Streaming EO UDP packets to {GUI_HOST}:{EO_GUI_PORT}")
        self.get_logger().info(f"Streaming IR UDP packets to {GUI_HOST}:{IR_GUI_PORT}")
        if MOTOR_ANGLE_BRIDGE_ENABLED:
            self.get_logger().info(f"Motor angle bridge enabled on UDP port {MOTOR_CONTROL_PORT}")
        else:
            self.get_logger().info("Motor angle bridge disabled; UDP 8000 remains available for the motor controller")
        if RECORDING_ENABLED:
            self.get_logger().info(
                f"Recording EO/IR videos to {RECORDING_DIR} every {RECORDING_SEGMENT_SECONDS} seconds"
            )
        if TRACKING_RECORDING_ENABLED:
            self.get_logger().info(
                f"Tracking recordings to {TRACKING_RECORDING_DIR} controlled by UDP {TRACKING_RECORDING_CONTROL_PORT}"
            )
        self._start_recording_http_server()

    def _on_eo_image(self, message: Image) -> None:
        try:
            self._eo.send_image(message)
            if not self._eo.first_image_logged:
                self.get_logger().info("EO first image sent!")
                self._eo.first_image_logged = True
        except Exception as exc:
            self.get_logger().error(f"Failed to forward EO image: {exc}")

    def _on_ir_image(self, message: Image) -> None:
        try:
            self._ir.send_image(message)
            if not self._ir.first_image_logged:
                self.get_logger().info("IR first image sent!")
                self._ir.first_image_logged = True
        except Exception as exc:
            self.get_logger().error(f"Failed to forward IR image: {exc}")

    def _on_eo_detection(self, message) -> None:
        try:
            self._eo.send_detection(message)
            if not self._eo.first_detection_logged:
                self.get_logger().info("EO first detection packet sent!")
                self._eo.first_detection_logged = True
        except Exception as exc:
            self.get_logger().error(f"Failed to forward EO detections: {exc}")

    def _on_ir_detection(self, message) -> None:
        try:
            self._ir.send_detection(message)
            if not self._ir.first_detection_logged:
                self.get_logger().info("IR first detection packet sent!")
                self._ir.first_detection_logged = True
        except Exception as exc:
            self.get_logger().error(f"Failed to forward IR detections: {exc}")

    def close(self) -> None:
        self._eo.close()
        self._ir.close()
        if self._motor is not None:
            self._motor.close()
        if self._recording_segment_scheduler is not None:
            self._recording_segment_scheduler.close()
        if self._tracking_control is not None:
            self._tracking_control.close()
        if self._tracking_recorder is not None:
            self._tracking_recorder.close()
        if self._recording_http_server is not None:
            self._recording_http_server.shutdown()
            self._recording_http_server.server_close()

    def _start_recording_http_server(self) -> None:
        if not RECORDING_ENABLED or not RECORDING_HTTP_ENABLED:
            return

        Path(RECORDING_DIR).mkdir(parents=True, exist_ok=True)
        handler = partial(RecordingVideoHandler, directory=RECORDING_DIR)
        self._recording_http_server = ThreadingHTTPServer(("0.0.0.0", RECORDING_HTTP_PORT), handler)
        self._recording_http_thread = threading.Thread(
            target=self._recording_http_server.serve_forever,
            name="recording-video-http",
            daemon=True,
        )
        self._recording_http_thread.start()
        self.get_logger().info(f"Recorded video player: http://0.0.0.0:{RECORDING_HTTP_PORT}/")


def main() -> None:
    rclpy.init()
    node = CameraUdpBridge()
    try:
        rclpy.spin(node)
    finally:
        node.close()
        node.destroy_node()
        rclpy.shutdown()


if __name__ == "__main__":
    main()
