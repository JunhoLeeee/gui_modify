using BroadcastControl.App.Models.Camera;

namespace BroadcastControl.App.Services;

// 카메라 프레임 수신 서비스의 공통 계약입니다.
// EO/IR 수신 포트 시작/중지와 프레임/탐지/상태 이벤트를 View 계층이 같은 방식으로 구독하게 합니다.
public interface ICameraFrameService : IDisposable
{
    event Action<ReceivedVideoFrame>? FrameReady;

    event Action<DetectionPacket>? DetectionsReceived;

    event Action<YoloStatusPacket>? StatusReceived;

    int ListeningPort { get; }

    bool Start(int port);

    void Stop();
}
