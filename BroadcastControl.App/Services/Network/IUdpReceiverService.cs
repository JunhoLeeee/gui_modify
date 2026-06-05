namespace BroadcastControl.App.Services;

// 범용 UDP 수신 서비스의 계약입니다.
// 포트 열기, 수신 패킷 이벤트, 수신 오류 이벤트를 구현체와 ViewModel 사이에서 고정합니다.
public interface IUdpReceiverService : IDisposable
{
    event EventHandler<byte[]>? PacketReceived;

    event EventHandler<string>? ReceiverError;

    int Port { get; }

    void Start();
}
