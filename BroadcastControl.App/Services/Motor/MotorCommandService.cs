using BroadcastControl.App.Models.Motor;

namespace BroadcastControl.App.Services;

// ViewModel이 직접 UDP 구현을 알지 않도록 감싸는 모터 명령 서비스입니다.
// 네트워크 설정 변경 시 endpoint를 바꾸고, 실제 전송은 UdpMotorControlService에 위임합니다.
public sealed class MotorCommandService : IMotorCommandService
{
    private readonly UdpMotorControlService _udpMotorControlService;

    public MotorCommandService(UdpMotorControlService? udpMotorControlService = null)
    {
        _udpMotorControlService = udpMotorControlService ?? new UdpMotorControlService();
    }

    public string Host => _udpMotorControlService.Host;

    public int Port => _udpMotorControlService.Port;

    public void ConfigureEndpoint(string? host, int? port = null, int? trackingRecordingControlPort = null)
    {
        _udpMotorControlService.ConfigureEndpoint(host, port, trackingRecordingControlPort);
    }

    public bool TrySendMotorCommandPacket(
        byte mode,
        byte tracking,
        byte trackId,
        bool isEoPrimary,
        MotorButtonMask btnMask,
        ushort panPos,
        ushort tiltPos,
        byte scanStep,
        byte manualStep,
        out string? error)
    {
        return _udpMotorControlService.TrySendMotorCommandPacket(
            mode,
            tracking,
            trackId,
            isEoPrimary,
            btnMask,
            panPos,
            tiltPos,
            scanStep,
            manualStep,
            out error);
    }

    public void Dispose()
    {
        _udpMotorControlService.Dispose();
    }
}
