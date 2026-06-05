using System.Net.Sockets;

namespace BroadcastControl.App.Services;

// 원시 UDP 바이트 배열을 지정한 host:port로 보내는 공통 송신 서비스입니다.
// 전송 실패 메시지를 out error로 돌려 UI 로그에 표시할 수 있게 합니다.
public sealed class UdpSenderService : IUdpSenderService
{
    private readonly UdpClient _udpClient = new();

    public bool TrySend(byte[] packet, string host, int port, out string? error)
    {
        try
        {
            _udpClient.Send(packet, packet.Length, host, port);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }
}
