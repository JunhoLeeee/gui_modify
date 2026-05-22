ARG BASE_IMAGE=ros:jazzy-ros-base
FROM ${BASE_IMAGE}

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3-opencv \
        ros-jazzy-cv-bridge \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY app/camera_udp_bridge.py /app/camera_udp_bridge.py

ENV PYTHONUNBUFFERED=1

CMD ["bash", "-lc", "source /opt/ros/jazzy/setup.bash && if [ -n \"${WORKSPACE_SETUP:-}\" ] && [ -f \"${WORKSPACE_SETUP}\" ]; then source \"${WORKSPACE_SETUP}\"; fi && python3 /app/camera_udp_bridge.py"]
