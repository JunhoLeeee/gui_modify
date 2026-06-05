using BroadcastControl.App.Models.Motor;

namespace BroadcastControl.App.Services;

// 모터 명령 전송 서비스가 제공해야 하는 기능 계약입니다.
// MainViewModel은 이 인터페이스를 통해 Jetson 주소 설정과 모터 명령 전송을 호출합니다.
public interface IMotorCommandService : IDisposable
{
    string Host { get; }

    int Port { get; }

    void ConfigureEndpoint(string? host, int? port = null, int? trackingRecordingControlPort = null);

    bool TrySendMotorCommandPacket(
        byte mode,
        byte tracking,
        byte trackId,
        bool isEoPrimary,
        MotorButtonMask btnMask,
        ushort panPos,
        ushort tiltPos,
        byte scanStep,
        byte manualStep,
        out string? error);
}
