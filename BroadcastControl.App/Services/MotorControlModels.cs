// GUI에서 Jetson/Thor로 보내는 모터 수동 조작 버튼 값을 정의하는 모델 파일이다.
// 각 방향 버튼은 하나의 비트로 표현되며, UdpMotorControlService가 이 값을 10B 모터 명령 패킷에 넣어 전송한다.
namespace BroadcastControl.App.Services;

[Flags]
public enum MotorButtonMask : byte
{
    // 버튼을 누르지 않은 상태다.
    None = 0,
    // Pan 오른쪽 이동.
    Right = 0x02,
    // Pan 왼쪽 이동.
    Left = 0x01,
    // Tilt 위쪽 이동.
    Up = 0x04,
    // Tilt 아래쪽 이동.
    Down = 0x08,
    // 중앙/정지/홈 계열 명령에 사용하는 비트다.
    Center = 0x10
}
