# JetsonThor.RosCameraBridge

Jetson에서 ROS2 영상 토픽과 YOLO detection 토픽을 구독한 뒤,
PC 운용통제 GUI가 받을 수 있는 UDP 패킷으로 변환해 전송하는 브릿지입니다.

## 역할

`gui_camera_bridge` 컨테이너는 다음 일을 담당합니다.

- ROS2 EO/IR 이미지 토픽 구독
- ROS2 YOLO detection 토픽 구독
- 이미지를 `SNTL` JPEG 청크 UDP 패킷으로 변환
- EO detection을 GUI용 `SNTL` type `0x10` 패킷으로 변환
- PC GUI로 EO `6000`, IR `6001` 포트 전송
- 녹화 파일 및 기본 로그/VLM 더미 파일 생성
- 녹화 영상 확인용 HTTP 서버 제공

이 브릿지는 YOLO를 직접 실행하지 않습니다. YOLO/추적 처리는 Jetson의 별도 ROS2 노드가 담당하고,
브릿지는 이미 발행된 `/tracks/eo`, `/tracks/ir` 토픽만 구독해서 GUI로 전달합니다.

## 기본 입력 토픽

| 데이터 | 기본 토픽 | 메시지 타입 |
| --- | --- | --- |
| EO image | `/camera/eo` | `sensor_msgs/msg/Image` |
| IR image | `/camera/ir` | `sensor_msgs/msg/Image` |
| EO track/detection | `/tracks/eo` | `sentinel_interfaces/msg/TrackedDetection2DArray` |
| IR track/detection | `/tracks/ir` | `sentinel_interfaces/msg/TrackedDetection2DArray` |

EO가 동작하지 않는 실험 상태에서도 IR은 `/camera/ir`만 정상 발행되면 GUI로 전송할 수 있습니다.

## 기본 출력 포트

| 출력 | 포트 |
| --- | --- |
| EO image + EO detection | `6000/udp` |
| IR image | `6001/udp` |
| 녹화 영상 HTTP 서버 | `8090/tcp` |

GUI에서 별도 수신하는 UDP 포트:

| 데이터 | 포트 |
| --- | --- |
| VLM result | `6002/udp` |
| Motor command | `8000/udp` |
| Motor status | `8001/udp` |
| Tracking recording control | `8010/udp` |

주의:

- Zybo -> Jetson IR 입력은 `5001/udp`를 사용합니다.
- Jetson bridge -> PC GUI IR 출력은 `6001/udp`를 사용합니다.
- 두 포트는 서로 다른 구간입니다.
- VLM 결과는 영상 패킷에 묶지 않고 `6002/udp`로 분리해 GUI가 별도 스레드에서 받을 수 있게 둡니다.
- 모터는 `8000/udp` 명령, `8001/udp` 상태 피드백을 사용합니다.
- GUI -> Thor 모터 패킷은 10B입니다: mode, tracking, track_id, btn_mask, pan_pos, tilt_pos, scan_step, manual_step. Thor -> GUI 모터 상태 패킷은 36B입니다.

## 파일 구성

| 파일 | 설명 |
| --- | --- |
| `Dockerfile` | `gui_camera_bridge` 이미지 빌드 파일 |
| `run_camera_udp_bridge.sh` | 컨테이너 빌드/실행 스크립트 |
| `app/camera_udp_bridge.py` | ROS2 토픽 구독 및 GUI UDP 전송 노드 |
| `fastdds_no_shm.xml` | 컨테이너 간 DDS shared memory 문제를 피하기 위한 FastDDS UDP-only 설정 파일 |

## 실행

기본 실행:

```bash
cd ~/LIG_DNA_GUI/JetsonThor.RosCameraBridge
bash ./run_camera_udp_bridge.sh
```

`GUI_HOST`를 직접 지정하지 않으면 스크립트가 `../BroadcastControl.App/LigDnaGui.config.json`의 `PcGuiHost` 값을 읽어 GUI 송출 대상 IP로 사용합니다.
GUI에서 GUI IP를 바꾸고 저장한 뒤 Jetson 쪽 코드에도 같은 설정 파일이 반영되어 있으면, 다음 브릿지 실행부터 새 GUI IP로 송출됩니다.

특정 IP를 임시로 강제하려면:

```bash
cd ~/LIG_DNA_GUI/JetsonThor.RosCameraBridge
GUI_HOST=192.168.1.94 bash ./run_camera_udp_bridge.sh
```

이미지를 새로 빌드하면서 실행:

```bash
cd ~/LIG_DNA_GUI/JetsonThor.RosCameraBridge
bash ./run_camera_udp_bridge.sh --build
```

스크립트 기본값:

```text
CONTAINER_NAME=gui_camera_bridge
IMAGE_NAME=gui_camera_bridge
WORKSPACE_DIR=/home/lig/gui_camera_ws
WORKSPACE_SETUP=/ros2_ws/install/local_setup.bash
GUI_CONFIG_FILE=../BroadcastControl.App/LigDnaGui.config.json
EO_GUI_PORT=6000
IR_GUI_PORT=6001
JETSON_RECORDING_DIR=/home/lig/Desktop/video
RECORDING_SEGMENT_SECONDS=60
RECORDING_HTTP_PORT=8090
FASTDDS_NO_SHM=true
```

## FastDDS no-shm

별도 Docker 컨테이너 사이에서 ROS2 토픽 이름은 보이지만 이미지 메시지를 실제로 받지 못하는 경우가 있었습니다.

확인된 증상:

```text
thor2 컨테이너에서는 /camera/ir header 수신 가능
gui_camera_bridge 컨테이너에서는 /camera/ir header 수신 불가
```

이 경우 FastDDS shared memory 전송 문제가 원인이 될 수 있습니다.
현재 `run_camera_udp_bridge.sh`는 기본적으로 shared memory를 끄고 UDPv4 transport만 사용하도록
저장소에 포함된 `fastdds_no_shm.xml`을 컨테이너에 마운트합니다.
파일이 없으면 실행 전에 같은 내용으로 다시 생성합니다.

기본 사용:

```bash
GUI_HOST=192.168.1.94 bash ./run_camera_udp_bridge.sh
```

FastDDS no-shm 설정을 끄고 실행:

```bash
FASTDDS_NO_SHM=false GUI_HOST=192.168.1.94 bash ./run_camera_udp_bridge.sh
```

## 녹화 저장 구조

기본 저장 위치:

```text
/home/lig/Desktop/video
```

저장 폴더 예:

```text
/home/lig/Desktop/video/
  20260513_145200/
    IR_COLOR_20260513_145200.mp4
    IR_GRAY_20260513_145200.mp4
    EO_20260513_145200.mp4
    system_log_20260513_145200.txt
    vlm_analysis_20260513_145200.txt
  Tracked/
    Tracking_20260513_145230.mp4
```

`Tracked` 폴더에는 GUI에서 VLM 위험 객체 tracking 상태가 켜진 동안의 별도 영상이 저장됩니다.
파일명은 `Tracking_촬영시작날짜및시간.mp4` 형식입니다.

녹화 기능을 끄고 브릿지만 실험:

```bash
RECORDING_ENABLED=false GUI_HOST=192.168.1.94 bash ./run_camera_udp_bridge.sh
```

## 상태 확인

### 컨테이너 실행 확인

```bash
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | grep gui_camera_bridge
```

### 브릿지 로그 확인

```bash
docker logs --tail 100 gui_camera_bridge
```

정상 로그:

```text
Streaming IR UDP packets to 192.168.1.94:6001
IR first image sent!
```

### Jetson ROS2 IR 입력 확인

```bash
docker exec thor2 bash -lc 'source /opt/ros/jazzy/setup.bash; source /ros2_ws/install/local_setup.bash; ros2 topic echo /camera/ir/rx_status --once'
```

정상 예:

```text
is_ok: true
message: IR receiver | packets=... frames=... dropped=...
published_frames: ...
```

### 브릿지 컨테이너에서 IR 메시지 확인

```bash
docker exec gui_camera_bridge bash -lc 'source /opt/ros/jazzy/setup.bash; source /ros2_ws/install/local_setup.bash >/dev/null 2>&1 || true; timeout 10 ros2 topic echo /camera/ir --once --field header'
```

### Jetson -> PC UDP 송신 확인

```bash
sudo tcpdump -ni any dst host 192.168.1.94 and udp port 6001
```

패킷이 찍히면 Jetson bridge가 PC GUI로 IR 영상을 보내고 있는 상태입니다.

## 문제 구간 판단

| 증상 | 의심 구간 |
| --- | --- |
| `tcpdump udp port 5001`에 입력 없음 | Zybo -> Jetson IR 송신 문제 |
| `rx_status`의 `published_frames`가 증가하지 않음 | `video_rx_node` 처리 문제 |
| `thor2`에서는 `/camera/ir` 수신, bridge에서는 수신 불가 | 컨테이너 간 DDS 전달 문제 |
| `IR first image sent!`는 뜨지만 PC에서 UDP 수신 안 됨 | Windows 방화벽, IP, 라우팅 문제 |
| Windows에서 UDP 수신은 되지만 GUI에 안 보임 | GUI 수신/디코딩/표시 문제 |
