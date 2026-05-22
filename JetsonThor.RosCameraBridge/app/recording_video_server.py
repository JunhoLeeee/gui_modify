#!/usr/bin/env python3
"""Serve recorded videos from the Jetson video folder for the GUI."""

from __future__ import annotations

import html
import json
import os
import threading
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import quote, urlparse
from datetime import datetime


VIDEO_FILE_EXTENSIONS = {".mp4", ".avi", ".mov", ".mkv", ".m4v"}
RECORDING_DIR = os.getenv("RECORDING_DIR", "/home/lig/Desktop/video")
RECORDING_HTTP_PORT = int(os.getenv("RECORDING_HTTP_PORT", "8090"))


def current_recording_segment_name() -> str:
    now = datetime.now().replace(second=0, microsecond=0)
    return now.strftime("%Y%m%d_%H%M%S")


def safe_segment_name(value: object | None) -> str:
    if not isinstance(value, str) or not value.strip():
        return current_recording_segment_name()

    cleaned = "".join(ch for ch in value.strip() if ch.isalnum() or ch in {"_", "-"})
    return cleaned or current_recording_segment_name()


def latest_existing_recording_segment_name(directory: Path) -> str:
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


def ensure_default_metadata_files(directory: Path, timestamp: str) -> None:
    segment_directory = directory / timestamp
    segment_directory.mkdir(parents=True, exist_ok=True)
    system_path = segment_directory / f"system_log_{timestamp}.txt"
    analysis_path = segment_directory / f"vlm_analysis_{timestamp}.txt"

    if not system_path.exists():
        system_path.write_text(default_system_log_text(timestamp), encoding="utf-8")

    if not analysis_path.exists():
        analysis_path.write_text(default_vlm_analysis_text(timestamp), encoding="utf-8")


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

    def _video_files(self) -> list[Path]:
        directory = Path(self.directory)
        return sorted(
            [
                item
                for item in directory.rglob("*")
                if item.is_file() and item.suffix.lower() in VIDEO_FILE_EXTENSIONS
            ],
            key=lambda item: item.stat().st_mtime,
            reverse=True,
        )

    def _send_video_list(self) -> None:
        payload = [
            {
                "name": item.relative_to(Path(self.directory)).as_posix(),
                "url": quote(item.relative_to(Path(self.directory)).as_posix(), safe="/"),
                "sizeBytes": item.stat().st_size,
                "modifiedUnixMs": int(item.stat().st_mtime * 1000),
            }
            for item in self._video_files()
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
        files = [item.relative_to(directory).as_posix() for item in self._video_files()]
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
</head>
<body>
  <h1>Recorded Videos</h1>
  <select id="fileList">{options}</select>
  {('<video id="player" controls src="' + initial_source + '" style="width:100%;max-height:80vh;background:#000"></video>') if files else '<p>No recorded videos.</p>'}
  <script>
    const fileList = document.getElementById('fileList');
    const player = document.getElementById('player');
    if (fileList && player) {{
      fileList.addEventListener('change', () => {{
        player.src = fileList.value;
        player.load();
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
        timestamp = safe_segment_name(requested_folder) if requested_folder else latest_existing_recording_segment_name(directory)
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


class RecordingFolderScheduler:
    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self._stop_event = threading.Event()
        self._thread = threading.Thread(
            target=self._run,
            name="recording-folder-scheduler",
            daemon=True,
        )
        self.directory.mkdir(parents=True, exist_ok=True)
        timestamp = current_recording_segment_name()
        ensure_default_metadata_files(self.directory, timestamp)
        print(f"Recording segment folder ready: {self.directory / timestamp}", flush=True)
        self._thread.start()

    def close(self) -> None:
        self._stop_event.set()
        self._thread.join(timeout=2.0)

    def _run(self) -> None:
        last_timestamp = current_recording_segment_name()
        while not self._stop_event.wait(1.0):
            timestamp = current_recording_segment_name()
            if timestamp == last_timestamp:
                continue

            last_timestamp = timestamp
            ensure_default_metadata_files(self.directory, timestamp)
            print(f"Recording segment folder ready: {self.directory / timestamp}", flush=True)


def main() -> None:
    directory = Path(RECORDING_DIR)
    directory.mkdir(parents=True, exist_ok=True)
    handler = partial(RecordingVideoHandler, directory=str(directory))
    server = ThreadingHTTPServer(("0.0.0.0", RECORDING_HTTP_PORT), handler)
    scheduler = RecordingFolderScheduler(directory)
    print(f"Serving {directory} at http://0.0.0.0:{RECORDING_HTTP_PORT}/", flush=True)
    try:
        server.serve_forever()
    finally:
        scheduler.close()


if __name__ == "__main__":
    main()
