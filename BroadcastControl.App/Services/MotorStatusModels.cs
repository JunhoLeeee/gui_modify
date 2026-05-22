// Jetson/Thor에서 GUI로 들어오는 모터 상태 패킷을 화면 표시용으로 담는 모델 파일이다.
// Pan과 Tilt 모터의 원본 Dynamixel 값, 온도, 전압, 속도, 목표 위치 등을 구조화해서 ViewModel이 읽기 쉽게 만든다.
namespace BroadcastControl.App.Services;

// 한 번 수신한 모터 상태 묶음이다. Tilt가 아직 오지 않은 구버전 패킷도 허용하기 위해 nullable로 둔다.
public readonly record struct MotorStatusSnapshot(
    MotorStatusPacket Pan,
    MotorStatusPacket? Tilt);

// 모터 하나의 상태값이다. 대부분 Dynamixel register 값을 그대로 담고, ViewModel에서 degree 표시로 변환한다.
public readonly record struct MotorStatusPacket(
    byte HardwareErrorStatus,
    byte PresentTemperature,
    ushort PresentInputVoltageRaw,
    uint PresentPosition,
    uint PresentVelocity,
    ushort PresentCurrentRaw,
    ushort PresentPwm,
    uint GoalPosition,
    uint GoalVelocity,
    byte Moving,
    byte MovingStatus,
    DateTime ReceivedAt)
{
    // Dynamixel 전압 raw 값은 0.1V 단위이므로 사람이 읽는 전압 값으로 변환한다.
    public double PresentInputVoltage => PresentInputVoltageRaw / 10.0;
}
