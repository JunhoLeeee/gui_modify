using System.Collections.ObjectModel;
using BroadcastControl.App.ViewModels;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroadcastControl.App.Infrastructure;
using BroadcastControl.App.Models.Camera;
using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.Services;
using BroadcastControl.App.ViewModels.Camera;
using BroadcastControl.App.ViewModels.Monitoring;
using BroadcastControl.App.ViewModels.Motor;
using BroadcastControl.App.ViewModels.Operation;
using BroadcastControl.App.ViewModels.Recording;

// 파일 역할:
// 녹화 상태 표시, 수동 녹화 토글, 시스템 로그 저장, Jetson 녹화 영상 목록 갱신을 관리합니다.
// Recording 패널의 표시등과 RecordedVideosView의 목록/재생 요청이 이 파일의 속성과 함수에 연결됩니다.

namespace BroadcastControl.App.ViewModels.Recording
{

/// <summary>
/// 현재 녹화 여부, 녹화 저장 경로, 화면에 보여줄 녹화 영상 목록을 보관합니다.
/// Recording 표시등과 RecordedVideosView의 영상 목록 바인딩에 사용됩니다.
/// </summary>
public sealed class RecordingViewModel : ViewModelBase
{
    private bool _isRecording;
    private string _recordingDirectory = string.Empty;

    public ViewportRecordingService ViewportRecordingService { get; } = new();

    public ObservableCollection<string> RecordedVideos { get; } = new();

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    public string RecordingDirectory
    {
        get => _recordingDirectory;
        set => SetProperty(ref _recordingDirectory, value);
    }

    public void DisposeServices()
    {
        ViewportRecordingService.Dispose();
    }
}
}

namespace BroadcastControl.App.ViewModels
{
// RecordingViewModel.cs 안에 둔 MainViewModel partial 영역입니다.
// 녹화와 로그 기능 코드를 Recording 폴더에 모아 기능 단위로 관리합니다.
public sealed partial class MainViewModel
{
    public bool IsRecordingActive =>
        IsSystemPoweredOn &&
        !_isRecordingSuppressed &&
        (IsManualRecordingEnabled || _isAutoRecordingLatched);

    public Brush RecordingIndicatorBrush => IsSystemPoweredOn && IsJetsonConnected ? RecordingOnBrush : RecordingOffBrush;

    public Brush RecordingTextBrush => IsSystemPoweredOn && IsJetsonConnected ? RecordingOnBrush : RecordingTextOffBrush;

    public double RecordingIndicatorOpacity => IsSystemPoweredOn && IsJetsonConnected ? 1.0 : 0.36;

    public bool IsManualRecordingEnabled
    {
        get => _isManualRecordingEnabled;
        private set
        {
            if (SetProperty(ref _isManualRecordingEnabled, value))
            {
                OnPropertyChanged(nameof(ManualRecordingButtonText));
                OnPropertyChanged(nameof(IsRecordingActive));
                OnPropertyChanged(nameof(RecordingIndicatorBrush));
                OnPropertyChanged(nameof(RecordingTextBrush));
                OnPropertyChanged(nameof(RecordingIndicatorOpacity));
            }
        }
    }

    public string ManualRecordingButtonText => IsRecordingActive ? Text["StopRecording"] : Text["StartRecording"];

    public void AppendImportantLog(string message)
    {
        AddSystemLogItem(new SystemLogItem(DateTime.Now.ToString("HH:mm:ss"), message));
    }

    public string BuildSystemLogSnapshot(DateTime startInclusive, DateTime endExclusive, bool includeAll)
    {
        var items = includeAll
            ? _systemLogHistory.OrderBy(item => item.CreatedAt).ToArray()
            : _systemLogHistory
                .Where(item => item.CreatedAt >= startInclusive && item.CreatedAt < endExclusive)
                .OrderBy(item => item.CreatedAt)
                .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("LIG DNA GUI System Log");
        builder.AppendLine($"Saved At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (!includeAll)
        {
            builder.AppendLine($"Window: {startInclusive:yyyy-MM-dd HH:mm:ss} - {endExclusive:yyyy-MM-dd HH:mm:ss}");
        }

        builder.AppendLine();

        if (items.Length == 0)
        {
            builder.AppendLine("No system log in this period.");
        }
        else
        {
            foreach (var item in items)
            {
                builder.AppendLine($"[{item.CreatedAt:yyyy-MM-dd HH:mm:ss}] {item.Message}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 현재 시스템 로그 목록을 Desktop의 CSV 파일로 저장합니다.
    /// 사용자가 Recording/Log 저장 버튼을 눌렀을 때 수동으로 호출됩니다.
    /// </summary>
    private void SaveSystemLogsToDesktop()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var filePath = Path.Combine(desktopPath, $"system_log_{timestamp}.txt");

            var builder = new StringBuilder();
            builder.AppendLine("LIG DNA GUI System Log");
            builder.AppendLine($"Saved At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();

            foreach (var log in _systemLogHistory)
            {
                builder.AppendLine($"[{log.Time}] {log.Message}");
            }

            File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(false));
            AppendImportantLog($"\uC2DC\uC2A4\uD15C \uB85C\uADF8\uB97C \uC800\uC7A5\uD588\uC2B5\uB2C8\uB2E4: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            AppendImportantLog($"\uC2DC\uC2A4\uD15C \uB85C\uADF8 \uC800\uC7A5\uC5D0 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4: {ex.Message}");
        }
    }

    private void ToggleManualRecording()
    {
        if (!IsSystemPoweredOn)
        {
            return;
        }

        if (IsRecordingActive)
        {
            _isRecordingSuppressed = true;
            _isAutoRecordingLatched = false;
            IsManualRecordingEnabled = false;
        }
        else
        {
            _isRecordingSuppressed = false;
            IsManualRecordingEnabled = true;
        }

        OnRecordingStateChanged();
    }

    private void OnRecordingStateChanged()
    {
        OnPropertyChanged(nameof(ManualRecordingButtonText));
        OnPropertyChanged(nameof(IsRecordingActive));
        OnPropertyChanged(nameof(RecordingIndicatorBrush));
        OnPropertyChanged(nameof(RecordingTextBrush));
        OnPropertyChanged(nameof(RecordingIndicatorOpacity));
    }

    private static void TrimCollection<T>(ObservableCollection<T> collection, int maxCount)
    {
        while (collection.Count > maxCount)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    private static void TrimList<T>(List<T> items, int maxCount)
    {
        while (items.Count > maxCount)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    private void AddSystemLogItem(SystemLogItem item)
    {
        _systemLogHistory.Insert(0, item);
        TrimList(_systemLogHistory, StoredLogItemLimit);

        SystemLogs.Insert(0, item);
        TrimCollection(SystemLogs, VisibleLogItemLimit);
    }
}
}
