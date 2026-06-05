using BroadcastControl.App.Models.Camera;

namespace BroadcastControl.App.Services;

// 카메라 UDP 수신 구현을 ViewModel에서 쓰기 쉬운 이벤트 형태로 감싸는 서비스입니다.
// 영상 프레임, 탐지 결과, YOLO 상태 이벤트를 MainWindow/CameraViewModel 쪽으로 전달합니다.
public sealed class CameraFrameService : ICameraFrameService
{
    private readonly UdpEncodedVideoReceiverService _receiver;

    public CameraFrameService(UdpEncodedVideoReceiverService receiver)
    {
        _receiver = receiver;
        _receiver.FrameReady += frame => FrameReady?.Invoke(frame);
        _receiver.DetectionsReceived += packet => DetectionsReceived?.Invoke(packet);
        _receiver.StatusReceived += packet => StatusReceived?.Invoke(packet);
    }

    public event Action<ReceivedVideoFrame>? FrameReady;

    public event Action<DetectionPacket>? DetectionsReceived;

    public event Action<YoloStatusPacket>? StatusReceived;

    public int ListeningPort => _receiver.ListeningPort;

    public bool Start(int port)
    {
        return _receiver.Start(port);
    }

    public void Stop()
    {
        _receiver.Stop();
    }

    public void Dispose()
    {
        _receiver.Dispose();
    }
}
