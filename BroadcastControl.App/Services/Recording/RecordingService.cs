using System.Windows;

namespace BroadcastControl.App.Services;

// GUI 화면 수동 녹화 기능을 ViewModel에 제공하는 서비스입니다.
// 실제 캡처와 AVI 저장은 ViewportRecordingService에 위임하고, 호출부에는 녹화 상태와 오류만 노출합니다.
public sealed class RecordingService : IRecordingService
{
    private readonly ViewportRecordingService _viewportRecordingService;

    public RecordingService(ViewportRecordingService? viewportRecordingService = null)
    {
        _viewportRecordingService = viewportRecordingService ?? new ViewportRecordingService();
    }

    public string? LastRecordingErrorMessage => _viewportRecordingService.LastRecordingErrorMessage;

    public int RecordedFrameCount => _viewportRecordingService.RecordedFrameCount;

    public string StartRecordingToDesktop(FrameworkElement target)
    {
        return _viewportRecordingService.StartRecordingToDesktop(target);
    }

    public string? StopRecording()
    {
        return _viewportRecordingService.StopRecording();
    }

    public void Dispose()
    {
        _viewportRecordingService.Dispose();
    }
}
