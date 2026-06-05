namespace BroadcastControl.App.Models.Motor;

// Jetson에서 받은 모터 상태 UDP 패킷을 Pan/Tilt 축별로 묶은 결과입니다.
// GUI의 모터 위치 표시와 System 연결 상태 판단에 사용됩니다.
public readonly record struct MotorStatusSnapshot(
    MotorStatusPacket Pan,
    MotorStatusPacket? Tilt);

// 모터 한 축의 상태값입니다.
// 위치 raw값, 속도, 전류, 전압, 온도, 이동 여부를 MotorControlViewModel에서 표시용 값으로 변환합니다.
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
    // Jetson/Zybo가 보낸 전압 raw값을 GUI 표시용 Volt 단위로 변환합니다.
    public double PresentInputVoltage => PresentInputVoltageRaw / 10.0;
}
