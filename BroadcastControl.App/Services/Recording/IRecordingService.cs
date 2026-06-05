using System.Windows;

namespace BroadcastControl.App.Services;

// 수동 화면 녹화 서비스의 계약입니다.
// MainViewModel은 이 인터페이스로 녹화 시작/중지와 마지막 오류 메시지를 확인합니다.
public interface IRecordingService : IDisposable
{
    string? LastRecordingErrorMessage { get; }

    int RecordedFrameCount { get; }

    string StartRecordingToDesktop(FrameworkElement target);

    string? StopRecording();
}
