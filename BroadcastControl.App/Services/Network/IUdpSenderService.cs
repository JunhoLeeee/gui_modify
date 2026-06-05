namespace BroadcastControl.App.Services;

// 범용 UDP 송신 서비스의 계약입니다.
// 패킷을 보낼 대상 주소와 포트를 호출 시점에 지정할 수 있게 합니다.
public interface IUdpSenderService : IDisposable
{
    bool TrySend(byte[] packet, string host, int port, out string? error);
}
