# LIG DNA GUI

LIG DNA GUI는 Windows WPF 기반 운용통제 화면과 Jetson ROS2 브릿지를 함께 사용하는 EO/IR 감시 GUI입니다.

GUI는 Jetson의 ROS2 토픽을 직접 구독하지 않습니다. Jetson에서 실행되는 브릿지가 ROS2 토픽을 UDP 패킷으로 변환해 PC GUI로 보내고, GUI는 영상, 탐지 결과, VLM 결과, 모터 상태를 받아 화면에 표시합니다.

## 현재 운용 구조

```text
Camera / Zybo
  -> Jetson ROS2 nodes
  -> gui_bridge:dev 또는 camera bridge
  -> PC GUI UDP ports
  -> BroadcastControl.App
```

GUI에서 Jetson 브릿지를 SSH로 자동 실행하는 기능은 제거했습니다. Network Settings에는 Jetson IP와 PC IP만 남겨 두고, 브릿지 컨테이너 실행과 종료는 Jetson에서 별도로 관리합니다.

## 주요 구성

| 경로 | 역할 |
| --- | --- |
| `BroadcastControl.App` | Windows WPF GUI |
| `JetsonThor.RosCameraBridge` | Jetson 카메라 UDP 브릿지 실행 스크립트와 Python 브릿지 |
| `BroadcastControl.UdpBenchmark` | UDP 수신 성능 확인용 도구 |
| `docs` | 구조와 운용 관련 보조 문서 |

## 네트워크 포트

| 포트 | 방향 | 기능 |
| --- | --- | --- |
| `6000/udp` | Jetson -> GUI | EO 영상, EO 탐지/추적 패킷 |
| `6001/udp` | Jetson -> GUI | IR 영상 |
| `6002/udp` | Jetson/VLM -> GUI | VLM 분석 결과 |
| `8000/udp` | GUI -> Jetson | 모터 제어 명령 패킷 |
| `8001/udp` | Jetson -> GUI | 모터 상태 패킷 |
| `8010/udp` | GUI -> Jetson camera bridge | VLM 위험 객체 tracking 녹화 시작/종료 제어 |
| `8088/tcp` | Mobile -> GUI | 모바일 위험 알림 HTTP/SSE |
| `8090/tcp` | GUI -> Jetson | 녹화 영상 목록/다운로드 HTTP 서버 |

## 모터 제어 패킷

GUI는 `8000/udp`로 10바이트 little-endian 패킷을 보냅니다.

| 바이트 | 필드 | 설명 |
| --- | --- | --- |
| `0` | mode | 자동/수동 모드 |
| `1` | tracking | 추적 명령. 추적 상태가 켜져 있고 위험 객체가 선택된 경우에만 `1` |
| `2` | track_id | 추적 대상 객체 ID. `0~254`, `0xff`는 auto |
| `3` | btn_mask | 방향 버튼 비트 |
| `4~5` | pan_pos | pan raw 위치 `0~4095` |
| `6~7` | tilt_pos | tilt raw 위치 `0~4095` |
| `8` | scan_step | 자동 스캔 step size |
| `9` | manual_step | 수동 조작 step size |

GUI의 step size 표시는 1도부터 10도까지 사용합니다. 모터로 보낼 때는 `deg / 360.0 * 4096.0` 기준으로 raw step 값으로 변환합니다.

모터 상태는 `8001/udp`로 받습니다. 현재 명세는 pan 18바이트와 tilt 18바이트가 이어진 36바이트 패킷입니다.

## 녹화 영상

GUI의 녹화 영상 목록은 Jetson의 녹화 HTTP 서버에서 가져옵니다.

기본 주소는 다음 형식입니다.

```text
http://{Jetson IP}:8090/api/videos
```

따라서 GUI 목록에 녹화 영상이 뜨려면 Jetson 쪽에서 `8090/tcp` HTTP 서버가 실행 중이어야 하고, PC에서 해당 Jetson IP로 접근 가능해야 합니다.

VLM이 위험 등급 객체를 감지해 GUI가 tracking=1을 보내면, GUI는 같은 Jetson IP의 `8010/udp`로 추적 녹화 제어 패킷도 함께 보냅니다.
Jetson camera bridge는 이 신호를 받아 `/home/lig/Desktop/video/Tracked` 폴더에 `Tracking_YYYYMMDD_HHMMSS.mp4` 형식의 별도 영상을 저장합니다.

## GUI IP 적용

GUI의 Network 영역에서 `GUI IP`를 저장하면 `LigDnaGui.config.json`의 `PcGuiHost` 값이 바뀝니다.
`JetsonThor.RosCameraBridge/run_camera_udp_bridge.sh`는 `GUI_HOST` 환경변수를 따로 주지 않은 경우 이 값을 읽어 다음 실행 시 송출 대상 IP로 사용합니다.

즉, Jetson 브릿지를 껐다가 다시 켤 때 다음처럼 실행하면 저장된 GUI IP가 자동 적용됩니다.

```bash
cd ~/LIG_DNA_GUI/JetsonThor.RosCameraBridge
bash ./run_camera_udp_bridge.sh
```

단, Windows GUI에서 저장한 설정 파일과 Jetson 쪽 `~/LIG_DNA_GUI/BroadcastControl.App/LigDnaGui.config.json`이 같은 값으로 반영되어 있어야 합니다.

## C# 파일 역할

| 파일 | 역할 |
| --- | --- |
| `BroadcastControl.App/App.xaml.cs` | 앱 시작점, 다크/라이트 테마 적용, 공통 브러시 리소스 갱신 |
| `BroadcastControl.App/AssemblyInfo.cs` | WPF 리소스 딕셔너리 탐색 위치 설정 |
| `BroadcastControl.App/MainWindow.xaml.cs` | 메인 화면 code-behind. 서비스 연결, UI 이벤트, 영상 표시, 탐지 오버레이, 녹화 영상 UI, 네트워크 설정 저장을 담당 |
| `BroadcastControl.App/Infrastructure/RelayCommand.cs` | ViewModel 명령을 WPF `ICommand`로 연결하는 공통 커맨드 클래스 |
| `BroadcastControl.App/Services/AppNetworkSettings.cs` | `LigDnaGui.config.json` 기반 네트워크 설정 로드/저장, 로컬 PC IPv4 목록 조회 |
| `BroadcastControl.App/Services/MobileAlertHubService.cs` | 모바일 브라우저용 위험 알림 HTTP/SSE 서버 |
| `BroadcastControl.App/Services/UdpEncodedVideoReceiverService.cs` | EO/IR UDP 영상 조각 조립, JPEG 디코딩, detection/status 패킷 전달 |
| `BroadcastControl.App/Services/UdpMotorControlService.cs` | GUI의 모터 제어 상태를 10바이트 UDP 패킷으로 직렬화해 Jetson으로 송신 |
| `BroadcastControl.App/Services/UdpMotorStatusReceiverService.cs` | Jetson에서 오는 모터 상태 패킷을 수신하고 pan/tilt 상태로 파싱 |
| `BroadcastControl.App/Services/UdpVlmResultReceiverService.cs` | VLM 분석 결과 UDP 수신, 전체 위험도와 객체별 위험도 파싱 |
| `BroadcastControl.App/Services/ViewportRecordingService.cs` | 현재 GUI 화면 영역을 로컬 동영상 파일로 저장 |
| `BroadcastControl.App/ViewModels/MainViewModel.cs` | 화면 상태와 명령의 중심 ViewModel. 모드, 추적 조건, 모터 raw/degree 변환, 로그, 언어, 테마 상태를 관리 |

## MVVM 구조

이 프로젝트는 WPF MVVM 구조를 기본으로 사용합니다.

| 계층 | 파일 | 설명 |
| --- | --- | --- |
| View | `MainWindow.xaml` | 실제 화면 레이아웃과 바인딩 정의 |
| View code-behind | `MainWindow.xaml.cs` | WPF 컨트롤, 마우스 입력, 영상 렌더링처럼 View에 가까운 작업 처리 |
| ViewModel | `MainViewModel.cs` | 화면에 표시할 상태와 버튼 명령 관리 |
| Services | `Services/*.cs` | UDP, HTTP, 녹화, 설정 파일 같은 외부 입출력 담당 |
| Infrastructure | `RelayCommand.cs` | MVVM 명령 연결 보조 |

영상 렌더링, 마우스 클릭 좌표, WPF `Image` 컨트롤, 녹화 미디어 컨트롤처럼 View 객체에 직접 접근해야 하는 기능은 `MainWindow.xaml.cs`에 남겨 두었습니다. 대신 모드 판단, 추적 가능 여부, 모터 값 변환, 표시 텍스트 같은 상태 중심 로직은 `MainViewModel.cs`에서 관리합니다.

## 빌드

```powershell
dotnet build .\BroadcastControl.App\BroadcastControl.App.csproj
```

현재 GUI는 Windows WPF 앱이므로 Windows Desktop을 포함한 .NET SDK가 필요합니다.
