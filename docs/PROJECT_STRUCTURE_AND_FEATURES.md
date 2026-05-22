# GUI SW 구조와 시스템 동작 설명

이 문서는 운용통제 GUI 시스템을 처음 보는 사람이 전체 구조를 빠르게 이해할 수 있도록 정리한 설명서입니다.
핵심은 **Jetson Thor에서 ROS2 토픽을 만들고, bridge가 그 토픽을 UDP 패킷으로 바꿔 PC GUI에 전달한다**는 점입니다.

## 1. 전체 시스템 흐름

```text
Zybo / Camera
-> Jetson Thor video_rx node
-> ROS2 영상 토픽 발행
   /camera/eo
   /camera/ir
-> 전처리 node
   /video/eo/preprocessed
-> YOLO / tracking node
   /tracks/eo
   /tracks/ir
-> gui_camera_bridge
   ROS2 토픽 구독
   GUI용 UDP 패킷으로 변환
-> PC 운용통제 GUI
   EO/IR 영상 표시
   바운딩 박스 표시
   VLM 결과 표시
   모터 제어/상태 표시
   녹화/알림 처리
```

GUI는 Jetson ROS2 토픽을 직접 구독하지 않습니다.  
대신 Jetson에서 실행되는 `gui_camera_bridge`가 ROS2 토픽을 구독하고, PC GUI가 이해할 수 있는 UDP 패킷으로 변환해 전송합니다.

## 2. 주요 실행 구성요소

| 구성요소 | 실행 위치 | 역할 |
| --- | --- | --- |
| `video_rx node` | Jetson Thor | Zybo/카메라 입력을 받아 `/camera/eo`, `/camera/ir` 토픽 발행 |
| `preprocessing node` | Jetson Thor | EO 영상을 전처리해 `/video/eo/preprocessed` 토픽 발행 |
| `YOLO / tracking node` | Jetson Thor | 영상에서 객체를 탐지/추적해 `/tracks/eo`, `/tracks/ir` 토픽 발행 |
| `gui_camera_bridge` | Jetson Thor Docker | ROS2 영상/트랙 토픽을 구독하고 UDP 패킷으로 PC GUI에 송신 |
| `BroadcastControl.App` | Windows PC | 운용통제 GUI. 영상, 디텍션, VLM, 모터, 녹화, 모바일 알림 표시 |
| `BroadcastControl.UdpBenchmark` | Windows PC | EO/IR UDP 수신 성능 비교용 도구 |

## 3. 주요 폴더와 파일

| 경로 | 설명 |
| --- | --- |
| `BroadcastControl.App` | WPF 기반 운용통제 GUI 본체 |
| `BroadcastControl.App/MainWindow.xaml` | GUI 화면 배치와 버튼/패널 레이아웃 |
| `BroadcastControl.App/MainWindow.xaml.cs` | UDP 수신 이벤트 연결, 화면 갱신, 영상/디텍션 표시 흐름 |
| `BroadcastControl.App/ViewModels/MainViewModel.cs` | GUI 상태, 모드, 모터 값, 로그, 언어/테마 상태 관리 |
| `BroadcastControl.App/Services/UdpEncodedVideoReceiverService.cs` | EO/IR 영상 및 디텍션 UDP 패킷 수신/조립/디코딩 |
| `BroadcastControl.App/Services/UdpMotorControlService.cs` | GUI에서 Thor로 모터 명령 UDP 송신 |
| `BroadcastControl.App/Services/UdpMotorStatusReceiverService.cs` | Thor에서 GUI로 오는 모터 상태 UDP 수신 |
| `BroadcastControl.App/Services/UdpVlmResultReceiverService.cs` | 외부 VLM 결과 UDP 수신 |
| `BroadcastControl.App/Services/MobileAlertHubService.cs` | 모바일 웹 알림 서버 |
| `JetsonThor.RosCameraBridge` | Jetson에서 실행하는 ROS2 -> UDP bridge |
| `JetsonThor.RosCameraBridge/app/camera_udp_bridge.py` | ROS2 토픽 구독, JPEG 인코딩, UDP 패킷 송신 |
| `JetsonThor.RosCameraBridge/run_camera_udp_bridge.sh` | bridge Docker 컨테이너 빌드/실행 스크립트 |
| `JetsonThor.RosCameraBridge/fastdds_no_shm.xml` | DDS shared memory 문제를 피하기 위한 FastDDS UDP-only 설정 |
| `BroadcastControl.UdpBenchmark` | UDP 영상 수신 성능 측정용 콘솔 프로그램 |

## 4. ROS2 토픽 구조

Jetson 내부에서는 ROS2 토픽으로 영상과 디텍션 정보가 오갑니다.

| 토픽 | 메시지 타입 | 의미 |
| --- | --- | --- |
| `/camera/eo` | `sensor_msgs/msg/Image` | EO 카메라 원본 영상 |
| `/camera/ir` | `sensor_msgs/msg/Image` | IR 카메라 원본 영상 |
| `/video/eo/preprocessed` | `sensor_msgs/msg/Image` | EO 전처리 영상. 필요 시 `EO_IMAGE_TOPIC`으로 선택 가능 |
| `/tracks/eo` | `sentinel_interfaces/msg/TrackedDetection2DArray` | EO 영상 기준 추적/디텍션 결과 |
| `/tracks/ir` | `sentinel_interfaces/msg/TrackedDetection2DArray` | IR 영상 기준 추적/디텍션 결과 |

현재 GUI로 전달하는 기본 영상 기준은 다음과 같습니다.

| GUI 화면 | bridge가 구독하는 기본 토픽 |
| --- | --- |
| EO 화면 | `/camera/eo` |
| IR 화면 | `/camera/ir` |
| EO 바운딩 박스 | `/tracks/eo` |
| IR 바운딩 박스 | `/tracks/ir` |

## 5. 네트워크 포트 구조

PC GUI와 Jetson Thor 사이의 UDP 포트는 기능별로 분리합니다. 이렇게 하면 영상, VLM, 모터 상태가 서로 다른 수신 루프에서 처리되어 한 기능의 지연이 다른 기능을 덜 막습니다.

| 포트 | 방향 | 내용 |
| --- | --- | --- |
| `6000/udp` | Jetson -> GUI | EO 영상 JPEG 청크 + EO 디텍션 |
| `6001/udp` | Jetson -> GUI | IR 영상 JPEG 청크 |
| `6002/udp` | 외부/VLM -> GUI | VLM 분석 결과 |
| `8000/udp` | GUI -> Jetson | 모터 커맨드 10B |
| `8001/udp` | Jetson -> GUI | 모터 상태 36B |
| `8088/tcp` | 모바일 브라우저 -> GUI | 모바일 위험 알림 웹앱 |
| `8090/tcp` | GUI -> Jetson bridge | Jetson 저장 영상 목록/재생 HTTP 서버 |

주의할 점:

- Zybo에서 Jetson `video_rx node`로 IR 영상이 들어올 때 쓰는 `5001/udp`와, Jetson bridge가 PC GUI로 IR 영상을 보내는 `6001/udp`는 서로 다른 구간입니다.
- `6000/6001`은 GUI가 수신하는 영상용 포트입니다.
- `8000/8001`은 모터 전용 포트입니다.

## 6. 영상 UDP 패킷 구조

EO/IR 영상은 JPEG로 인코딩된 뒤 여러 UDP 청크로 나뉘어 전송됩니다. GUI는 같은 `frame_id`를 가진 청크를 모아서 JPEG를 복원하고 화면에 표시합니다.

### 6.1 공통 영상 헤더

영상 패킷은 15B 헤더 뒤에 JPEG payload가 붙습니다.

| offset | 크기 | 필드 | 설명 |
| --- | --- | --- | --- |
| `0` | 4B | `magic` | ASCII `SNTL` |
| `4` | 1B | `type` | `0x01` EO 영상, `0x02` IR 영상 |
| `5` | 4B | `frame_id` | uint32 little-endian |
| `9` | 2B | `chunk_idx` | uint16 little-endian |
| `11` | 2B | `total_chunks` | uint16 little-endian |
| `13` | 2B | `payload_size` | uint16 little-endian |
| `15` | 가변 | `payload` | JPEG 일부 데이터 |

### 6.2 GUI 조립 방식

```text
1. UDP 패킷 수신
2. magic이 SNTL인지 확인
3. type이 0x01 또는 0x02인지 확인
4. frame_id 기준으로 chunk 저장
5. total_chunks 개수만큼 모두 모이면 payload를 chunk_idx 순서대로 concat
6. concat된 JPEG 바이트를 디코딩
7. WPF ImageSource로 변환해 화면 갱신
```

## 7. 디텍션 UDP 패킷 구조

EO 디텍션은 EO 영상과 같은 `6000/udp` 포트로 들어옵니다. 영상 청크와 디텍션은 `type` 값으로 구분합니다.

| type | 의미 |
| --- | --- |
| `0x01` | EO 영상 청크 |
| `0x10` | EO 디텍션 패킷 |

### 7.1 디텍션 공통 헤더

| offset | 크기 | 필드 | 설명 |
| --- | --- | --- | --- |
| `0` | 4B | `magic` | ASCII `SNTL` |
| `4` | 1B | `type` | `0x10` |
| `5` | 4B | `frame_id` | uint32 little-endian |
| `9` | 4B | `stamp_sec` | uint32 little-endian |
| `13` | 4B | `stamp_nsec` | uint32 little-endian |

### 7.2 Detection2D 블록

현재 코드에서는 일반 detection 블록을 읽을 수 있도록 남겨두었지만, bridge는 tracking 결과를 주로 보냅니다.

| 필드 | 크기 | 설명 |
| --- | --- | --- |
| `class_name` | 16B | UTF-8, null padding |
| `score` | 4B | float32 little-endian |
| `x1` | 4B | float32 little-endian |
| `y1` | 4B | float32 little-endian |
| `x2` | 4B | float32 little-endian |
| `y2` | 4B | float32 little-endian |

총 크기는 `36B`입니다.

### 7.3 TrackedDetection2D 블록

| 필드 | 크기 | 설명 |
| --- | --- | --- |
| `track_id` | 4B | int32 little-endian |
| `class_id` | 4B | int32 little-endian |
| `class_name` | 16B | UTF-8, null padding |
| `score` | 4B | float32 little-endian |
| `x1` | 4B | float32 little-endian |
| `y1` | 4B | float32 little-endian |
| `x2` | 4B | float32 little-endian |
| `y2` | 4B | float32 little-endian |

총 크기는 `44B`입니다.

GUI는 `track_id`를 객체 ID로 사용합니다. 이 값은 화면 표시와 추적 대상 판단에 사용할 수 있습니다.

## 8. 모터 UDP 패킷 구조

모터는 영상 포트와 분리되어 있습니다.

### 8.1 GUI -> Thor 모터 명령

GUI는 `8000/udp`로 10B 패킷을 보냅니다.

| offset | 크기 | 필드 | 설명 |
| --- | --- | --- | --- |
| `0` | 1B | `mode` | `0` scan/auto, `1` manual |
| `1` | 1B | `tracking` | `0` off, `1` on |
| `2` | 1B | `track_id` | `0~254` object id, `0xff` auto |
| `3` | 1B | `btn_mask` | 방향 버튼 bit mask |
| `4` | 2B | `pan_pos` | uint16 little-endian, Dynamixel 0~4095 |
| `6` | 2B | `tilt_pos` | uint16 little-endian, Dynamixel 0~4095 |
| `8` | 1B | `scan_step` | uint8, 1~10 |
| `9` | 1B | `manual_step` | uint8, 1~10 |

`btn_mask` 비트:

| 값 | 의미 |
| --- | --- |
| `0x01` | PAN +, 오른쪽 |
| `0x02` | PAN -, 왼쪽 |
| `0x04` | TILT +, 위 |
| `0x08` | TILT -, 아래 |

GUI의 pan/tilt 각도 입력값은 내부에서 Dynamixel 위치값 `0~4095`로 변환되어 전송됩니다.
자동 추적 상태에서는 GUI가 현재 화면에서 가장 위험도가 높은 YOLO 객체 ID를 `track_id`로 전송합니다.
사용자가 큰 영상 화면의 바운딩 박스 안을 클릭하면 해당 좌표에 있는 YOLO 객체 ID가 즉시 `track_id`로 전송되어 모터가 그 객체를 추적할 수 있습니다.

### 8.2 Thor -> GUI 모터 상태

Thor는 `8001/udp`로 36B 패킷을 보냅니다.

| offset | 크기 | 필드 | 설명 |
| --- | --- | --- | --- |
| `0` | 1B | `pan_moving` | pan 이동 여부 |
| `1` | 1B | `pan_moving_status` | pan 이동 상세 상태 |
| `2` | 2B | `pan_pwm` | uint16 little-endian |
| `4` | 2B | `pan_current` | uint16 little-endian |
| `6` | 4B | `pan_velocity` | uint32 little-endian |
| `10` | 4B | `pan_position` | uint32 little-endian, 0~4095 |
| `14` | 2B | `pan_voltage` | uint16 little-endian |
| `16` | 1B | `pan_temperature` | uint8 |
| `17` | 1B | `pan_hw_error` | uint8 |
| `18` | 1B | `tilt_moving` | tilt 이동 여부 |
| `19` | 1B | `tilt_moving_status` | tilt 이동 상세 상태 |
| `20` | 2B | `tilt_pwm` | uint16 little-endian |
| `22` | 2B | `tilt_current` | uint16 little-endian |
| `24` | 4B | `tilt_velocity` | uint32 little-endian |
| `28` | 4B | `tilt_position` | uint32 little-endian, 0~4095 |
| `32` | 2B | `tilt_voltage` | uint16 little-endian |
| `34` | 1B | `tilt_temperature` | uint8 |
| `35` | 1B | `tilt_hw_error` | uint8 |

GUI는 `pan_position`, `tilt_position`을 각도 값으로 변환해 Motor Position 창에 표시합니다.

## 9. VLM 결과 수신

VLM 결과는 영상 패킷과 섞지 않고 `6002/udp`로 따로 받습니다.  
이 포트를 분리한 이유는 VLM 분석 결과가 영상보다 늦게 도착할 수 있고, 큰 텍스트나 외부 모델 지연이 영상 표시를 막지 않게 하기 위해서입니다.

현재 GUI 수신기는 다음 형태를 처리할 수 있습니다.

| 형태 | 설명 |
| --- | --- |
| JSON 문자열 | `threatLevel`, `analysis`, `detectionSummary`, `frameId` 같은 필드 사용 가능 |
| 일반 텍스트 | 전체 문자열을 VLM 분석 메시지로 표시 |
| `VLMR` prefix + UTF-8 텍스트 | 앞 4B `VLMR`을 제거하고 본문 해석 |

VLM이 객체별 위험도를 보낼 때는 `objectThreats`, `trackThreats`, `detections`, `tracks`, `objects` 배열 중 하나에
`objectId`/`trackId`와 `threatLevel`/`riskLevel` 값을 넣을 수 있습니다. GUI는 객체별 위험도 중 가장 높은 값을 시스템 위험 등급으로 표시하고,
바운딩 박스 색상은 낮음=초록, 중간=노랑, 높음=빨강으로 표시합니다.

## 10. 녹화와 저장 구조

Jetson bridge는 `/home/lig/Desktop/video` 아래에 시간별 폴더를 만들고 EO/IR 영상과 기본 로그 파일을 저장합니다.

예시:

```text
/home/lig/Desktop/video/
  20260513_145200/
    EO_20260513_145200.mp4
    IR_COLOR_20260513_145200.mp4
    IR_GRAY_20260513_145200.mp4
    system_log_20260513_145200.txt
    vlm_analysis_20260513_145200.txt
```

GUI의 “녹화 영상 확인” 기능은 Jetson bridge의 HTTP 서버(`8090/tcp`)를 통해 이 폴더 목록과 영상을 확인합니다.

## 11. 실행 순서

### 11.1 Jetson에서 ROS2 토픽 확인

```bash
docker exec thor2 bash -lc 'source /opt/ros/jazzy/setup.bash; source /ros2_ws/install/local_setup.bash; ros2 topic list'
docker exec thor2 bash -lc 'source /opt/ros/jazzy/setup.bash; source /ros2_ws/install/local_setup.bash; timeout 10 ros2 topic hz /camera/ir'
docker exec thor2 bash -lc 'source /opt/ros/jazzy/setup.bash; source /ros2_ws/install/local_setup.bash; timeout 10 ros2 topic hz /video/eo/preprocessed'
```

### 11.2 Jetson에서 bridge 실행

```bash
cd ~/LIG_DNA_GUI/JetsonThor.RosCameraBridge

GUI_HOST=192.168.1.94 \
JETSON_RECORDING_DIR=/home/lig/Desktop/video \
RECORDING_SEGMENT_SECONDS=60 \
RECORDING_HTTP_PORT=8090 \
bash ./run_camera_udp_bridge.sh --build
```

### 11.3 PC에서 GUI 실행

Visual Studio에서 `BroadcastControl.App` 프로젝트를 실행합니다.

GUI는 실행 후 다음 포트들을 사용합니다.

```text
6000/udp  EO 영상 + EO 디텍션 수신
6001/udp  IR 영상 수신
6002/udp  VLM 결과 수신
8000/udp  모터 명령 송신
8001/udp  모터 상태 수신
8088/tcp  모바일 위험 알림 웹앱
```

## 12. 문제 확인 명령어

### bridge 컨테이너 실행 여부

```bash
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | grep gui_camera_bridge
docker logs --tail 100 gui_camera_bridge
```

### Jetson에서 PC로 UDP가 나가는지 확인

```bash
sudo tcpdump -ni any dst host 192.168.1.94 and udp and \( port 6000 or port 6001 \)
```

### PC에서 UDP가 들어오는지 확인

PowerShell에서 GUI를 끄고 테스트합니다. GUI가 이미 같은 포트를 잡고 있으면 테스트 수신기가 포트를 열 수 없습니다.

```powershell
$udp = New-Object System.Net.Sockets.UdpClient(6001)
$ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
while ($true) {
  $bytes = $udp.Receive([ref]$ep)
  Write-Host "$($ep.Address):$($ep.Port) length=$($bytes.Length)"
}
```

### 모터 상태 포트 확인

```powershell
$udp = New-Object System.Net.Sockets.UdpClient(8001)
$ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
while ($true) {
  $bytes = $udp.Receive([ref]$ep)
  Write-Host "$($ep.Address):$($ep.Port) length=$($bytes.Length)"
}
```

정상 모터 상태 패킷은 `length=36`으로 들어와야 합니다.

## 13. 설계 판단 요약

- GUI는 ROS2를 직접 구독하지 않고 Jetson bridge가 변환한 UDP만 받습니다.
- 영상은 대역폭과 실시간성이 중요하므로 JPEG 청크 UDP로 전송합니다.
- 디텍션은 영상과 같은 EO 포트 `6000`에 싣고 `type`으로 구분합니다.
- VLM은 지연이 크고 텍스트 중심이므로 `6002`로 분리합니다.
- 모터 명령/상태는 제어 안정성을 위해 `8000/8001`로 영상과 분리합니다.
- FastDDS shared memory 문제를 피하기 위해 bridge 컨테이너는 `fastdds_no_shm.xml`을 사용해 UDP-only DDS transport를 적용합니다.
