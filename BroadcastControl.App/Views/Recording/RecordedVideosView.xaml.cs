using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BroadcastControl.App.Models.Camera;
using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.ViewModels;

// 파일 역할:
// Jetson 녹화 HTTP 서버에서 영상 목록을 읽고 로컬 캐시로 내려받아 재생합니다.
// 폴더 이동, 재생/일시정지, 재생 위치 슬라이더, 영상 확대/이동 UI 이벤트를 처리합니다.

namespace BroadcastControl.App.Views.Recording
{
public partial class RecordedVideosView : UserControl
{
    public RecordedVideosView()
    {
        InitializeComponent();
    }

    public Border RecordedVideosPanelElement => RecordedVideosPanel;
    public TextBlock RecordedVideosStatusTextElement => RecordedVideosStatusText;
    public ListBox RecordedVideoListElement => RecordedVideoList;
    public Grid RecordedVideoViewportElement => RecordedVideoViewport;
    public MediaElement RecordedVideoPlayerElement => RecordedVideoPlayer;
    public ScaleTransform RecordedVideoScaleTransformElement => RecordedVideoScaleTransform;
    public TranslateTransform RecordedVideoTranslateTransformElement => RecordedVideoTranslateTransform;
    public Border RecordedVideoZoomMiniMapElement => RecordedVideoZoomMiniMap;
    public Rectangle RecordedVideoMiniMapViewportElement => RecordedVideoMiniMapViewport;
    public TextBlock RecordedVideoCurrentTimeTextElement => RecordedVideoCurrentTimeText;
    public Slider RecordedVideoPositionSliderElement => RecordedVideoPositionSlider;
    public TextBlock RecordedVideoDurationTextElement => RecordedVideoDurationText;
    public Button RecordedVideoZoomResetButtonElement => RecordedVideoZoomResetButton;
    public Slider RecordedVideoZoomSliderElement => RecordedVideoZoomSlider;
    public ComboBox PlaybackSpeedComboElement => PlaybackSpeedCombo;

    public event RoutedEventHandler? RefreshRecordedVideosClicked;
    public event RoutedEventHandler? CloseRecordedVideosClicked;
    public event SelectionChangedEventHandler? RecordedVideoSelectionChanged;
    public event MouseWheelEventHandler? RecordedVideoViewportMouseWheel;
    public event MouseButtonEventHandler? RecordedVideoViewportMouseLeftButtonDown;
    public event MouseEventHandler? RecordedVideoViewportMouseMove;
    public event MouseButtonEventHandler? RecordedVideoViewportMouseLeftButtonUp;
    public event RoutedEventHandler? RecordedVideoMediaOpened;
    public event RoutedEventHandler? RecordedVideoMediaEnded;
    public event EventHandler<ExceptionRoutedEventArgs>? RecordedVideoMediaFailed;
    public event RoutedPropertyChangedEventHandler<double>? RecordedVideoPositionSliderValueChanged;
    public event MouseButtonEventHandler? RecordedVideoPositionSliderPreviewMouseLeftButtonDown;
    public event MouseButtonEventHandler? RecordedVideoPositionSliderPreviewMouseLeftButtonUp;
    public event RoutedEventHandler? RecordedVideoPlayClicked;
    public event RoutedEventHandler? RecordedVideoPauseClicked;
    public event RoutedEventHandler? RecordedVideoStopClicked;
    public event RoutedEventHandler? RecordedVideoZoomResetClicked;
    public event RoutedPropertyChangedEventHandler<double>? RecordedVideoZoomSliderValueChanged;
    public event SelectionChangedEventHandler? PlaybackSpeedSelectionChanged;

    private void RefreshRecordedVideosButton_OnClick(object sender, RoutedEventArgs e) => RefreshRecordedVideosClicked?.Invoke(sender, e);
    private void CloseRecordedVideosButton_OnClick(object sender, RoutedEventArgs e) => CloseRecordedVideosClicked?.Invoke(sender, e);
    private void RecordedVideoList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RecordedVideoSelectionChanged?.Invoke(sender, e);
    private void RecordedVideoViewport_OnMouseWheel(object sender, MouseWheelEventArgs e) => RecordedVideoViewportMouseWheel?.Invoke(sender, e);
    private void RecordedVideoViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => RecordedVideoViewportMouseLeftButtonDown?.Invoke(sender, e);
    private void RecordedVideoViewport_OnMouseMove(object sender, MouseEventArgs e) => RecordedVideoViewportMouseMove?.Invoke(sender, e);
    private void RecordedVideoViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RecordedVideoViewportMouseLeftButtonUp?.Invoke(sender, e);
    private void RecordedVideoPlayer_OnMediaOpened(object sender, RoutedEventArgs e) => RecordedVideoMediaOpened?.Invoke(sender, e);
    private void RecordedVideoPlayer_OnMediaEnded(object sender, RoutedEventArgs e) => RecordedVideoMediaEnded?.Invoke(sender, e);
    private void RecordedVideoPlayer_OnMediaFailed(object sender, ExceptionRoutedEventArgs e) => RecordedVideoMediaFailed?.Invoke(sender, e);
    private void RecordedVideoPositionSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RecordedVideoPositionSliderValueChanged?.Invoke(sender, e);
    private void RecordedVideoPositionSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => RecordedVideoPositionSliderPreviewMouseLeftButtonDown?.Invoke(sender, e);
    private void RecordedVideoPositionSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RecordedVideoPositionSliderPreviewMouseLeftButtonUp?.Invoke(sender, e);
    private void RecordedVideoPlayButton_OnClick(object sender, RoutedEventArgs e) => RecordedVideoPlayClicked?.Invoke(sender, e);
    private void RecordedVideoPauseButton_OnClick(object sender, RoutedEventArgs e) => RecordedVideoPauseClicked?.Invoke(sender, e);
    private void RecordedVideoStopButton_OnClick(object sender, RoutedEventArgs e) => RecordedVideoStopClicked?.Invoke(sender, e);
    private void RecordedVideoZoomResetButton_OnClick(object sender, RoutedEventArgs e) => RecordedVideoZoomResetClicked?.Invoke(sender, e);
    private void RecordedVideoZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RecordedVideoZoomSliderValueChanged?.Invoke(sender, e);
    private void PlaybackSpeedCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => PlaybackSpeedSelectionChanged?.Invoke(sender, e);
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
    private void HandleRecordingActiveStateChanged()
    {
        if (_viewModel.IsRecordingActive)
        {
            if (_isViewportRecordingActive)
            {
                return;
            }

            var filePath = _viewportRecordingService.StartRecordingToDesktop(CameraActiveView.CameraPanelElement);
            _isViewportRecordingActive = true;
            _viewModel.AppendImportantLog($"화면 녹화를 시작했습니다: {filePath}");
            return;
        }

        if (!_isViewportRecordingActive)
        {
            return;
        }

        var savedPath = _viewportRecordingService.StopRecording();
        _isViewportRecordingActive = false;
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            if (System.IO.File.Exists(savedPath))
            {
                _viewModel.AppendImportantLog($"화면 녹화 영상을 저장했습니다: {savedPath} ({_viewportRecordingService.RecordedFrameCount} frames)");
            }
            else if (!string.IsNullOrWhiteSpace(_viewportRecordingService.LastRecordingErrorMessage))
            {
                _viewModel.AppendImportantLog($"화면 녹화 영상 저장에 실패했습니다: {_viewportRecordingService.LastRecordingErrorMessage}");
            }
            else
            {
                _viewModel.AppendImportantLog($"화면 녹화 파일이 생성되지 않았습니다: {savedPath} ({_viewportRecordingService.RecordedFrameCount} frames)");
            }
        }
    }

    private async void OpenRecordedVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideosActiveView.RecordedVideosPanelElement.Visibility = Visibility.Visible;
        await LoadRecordedVideosAsync();
        e.Handled = true;
    }

    private async void RefreshRecordedVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadRecordedVideosAsync();
        e.Handled = true;
    }

    private void CloseRecordedVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        _recordedVideoPositionTimer.Stop();
        RecordedVideosActiveView.RecordedVideoPlayerElement.Stop();
        RecordedVideosActiveView.RecordedVideoPlayerElement.Source = null;
        RecordedVideosActiveView.RecordedVideosPanelElement.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void RecordedVideoList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordedVideosActiveView.RecordedVideoListElement.SelectedItem is not RecordedVideoItem item)
        {
            return;
        }

        if (item.IsBack)
        {
            _recordedVideoSelectedFolder = null;
            ShowRecordedVideoFolderList();
            return;
        }

        if (item.IsFolder)
        {
            _recordedVideoSelectedFolder = item.Folder;
            ShowRecordedVideoFolderContents(item.Folder);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            return;
        }

        try
        {
            _recordedVideoPositionTimer.Stop();
            RecordedVideosActiveView.RecordedVideoPlayerElement.Stop();
            RecordedVideosActiveView.RecordedVideoPlayerElement.Source = null;
            ResetRecordedVideoPositionUi();
            RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = $"{item.DisplayName} 내려받는 중...";

            var localPath = await EnsureRecordedVideoCachedAsync(item);
            RecordedVideosActiveView.RecordedVideoPlayerElement.Source = new Uri(localPath, UriKind.Absolute);
            ResetRecordedVideoZoom();
            ApplyRecordedVideoPlaybackSpeed();
            RecordedVideosActiveView.RecordedVideoPlayerElement.Play();
            _recordedVideoPositionTimer.Start();
            RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = item.DisplayName;
        }
        catch (Exception ex)
        {
            RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = "영상을 재생할 수 없습니다.";
            _viewModel.AppendImportantLog($"녹화 영상 재생 준비에 실패했습니다: {ex.Message}");
        }
    }

    private void RecordedVideoPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyRecordedVideoPlaybackSpeed();
        RecordedVideosActiveView.RecordedVideoPlayerElement.Play();
        _recordedVideoPositionTimer.Start();
    }

    private void RecordedVideoPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideosActiveView.RecordedVideoPlayerElement.Pause();
    }

    private void RecordedVideoStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideosActiveView.RecordedVideoPlayerElement.Stop();
        _recordedVideoPositionTimer.Stop();
        ResetRecordedVideoPositionUi();
    }

    private void PlaybackSpeedCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyRecordedVideoPlaybackSpeed();
    }

    private async Task LoadRecordedVideosAsync()
    {
        var baseUri = GetRecordedVideoBaseUri();
        var apiUri = new Uri(baseUri, "api/videos");
        RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = "목록을 불러오는 중...";

        try
        {
            using var stream = await RecordedVideoHttpClient.GetStreamAsync(apiUri);
            var videos = await JsonSerializer.DeserializeAsync<List<RecordedVideoItem>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            MarkJetsonMessageReceived();
            videos ??= new List<RecordedVideoItem>();

            foreach (var video in videos)
            {
                video.Folder = GetRecordedVideoFolder(video.Name);
                video.DisplayName = GetRecordedVideoFileName(video.Name);
                if (Uri.TryCreate(video.Url, UriKind.Absolute, out _))
                {
                    continue;
                }

                video.Url = new Uri(baseUri, video.Url).ToString();
            }

            _recordedVideoFiles.Clear();
            _recordedVideoFiles.AddRange(videos);

            if (!string.IsNullOrWhiteSpace(_recordedVideoSelectedFolder) &&
                _recordedVideoFiles.Any(video => string.Equals(video.Folder, _recordedVideoSelectedFolder, StringComparison.Ordinal)))
            {
                ShowRecordedVideoFolderContents(_recordedVideoSelectedFolder);
            }
            else
            {
                _recordedVideoSelectedFolder = null;
                ShowRecordedVideoFolderList();
            }
        }
        catch (Exception ex)
        {
            RecordedVideosActiveView.RecordedVideoListElement.ItemsSource = null;
            RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = "목록을 불러오지 못했습니다.";
            _viewModel.AppendImportantLog($"녹화 영상 목록을 불러오지 못했습니다: {ex.Message}");
        }
    }

    private Uri GetRecordedVideoBaseUri()
    {
        var videoUrl = _networkSettings.RecordedVideoUrl;

        if (!videoUrl.EndsWith("/", StringComparison.Ordinal))
        {
            videoUrl += "/";
        }

        return new Uri(videoUrl, UriKind.Absolute);
    }

    private void ShowRecordedVideoFolderList()
    {
        var folders = _recordedVideoFiles
            .GroupBy(video => video.Folder)
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RecordedVideoItem
            {
                Folder = group.Key,
                DisplayName = $"[폴더] {group.Key} ({group.Count()}개)",
                IsFolder = true
            })
            .ToList();

        RecordedVideosActiveView.RecordedVideoListElement.ItemsSource = folders;
        RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = folders.Count == 0
            ? "저장된 영상 폴더가 아직 없습니다."
            : $"{folders.Count}개 폴더";
    }

    private void ShowRecordedVideoFolderContents(string folder)
    {
        var videos = _recordedVideoFiles
            .Where(video => string.Equals(video.Folder, folder, StringComparison.Ordinal))
            .OrderBy(video => video.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<RecordedVideoItem>
        {
            new()
            {
                DisplayName = "[상위 폴더]",
                IsBack = true
            }
        };
        items.AddRange(videos);

        RecordedVideosActiveView.RecordedVideoListElement.ItemsSource = items;
        RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = $"{folder} / {videos.Count}개 영상";
    }

    private static string GetRecordedVideoFolder(string name)
    {
        var normalized = name.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex > 0
            ? normalized[..separatorIndex]
            : "기존 영상";
    }

    private static string GetRecordedVideoFileName(string name)
    {
        var normalized = name.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 && separatorIndex < normalized.Length - 1
            ? normalized[(separatorIndex + 1)..]
            : normalized;
    }

    private async void RecordingMetadataTimer_OnTick(object? sender, EventArgs e)
    {
        var windowEnd = DateTime.Now;
        var windowStart = _recordingMetadataWindowStart == default
            ? windowEnd.AddMinutes(-1)
            : _recordingMetadataWindowStart;
        _recordingMetadataWindowStart = windowEnd;

        await SaveRecordingMetadataAsync(
            windowStart,
            windowEnd,
            manual: false,
            includeSystemLog: true);
    }

    private async void ViewModel_OnManualSystemLogSaveRequested(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        await SaveRecordingMetadataAsync(
            now,
            now,
            manual: true,
            includeSystemLog: true);
    }

    private async Task SaveRecordingMetadataAsync(
        DateTime windowStart,
        DateTime windowEnd,
        bool manual,
        bool includeSystemLog)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["manual"] = manual
            };

            if (includeSystemLog)
            {
                payload["systemLogText"] = _viewModel.BuildSystemLogSnapshot(windowStart, windowEnd, includeAll: manual);
            }

            var apiUri = new Uri(GetRecordedVideoBaseUri(), "api/logs");
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await RecordedVideoHttpClient.PostAsync(apiUri, content);
            response.EnsureSuccessStatusCode();

            if (manual)
            {
                _viewModel.AppendImportantLog("시스템 로그를 현재 영상 폴더의 CSV 파일로 저장했습니다.");
            }
        }
        catch (Exception ex)
        {
            var modeText = manual ? "수동" : "자동";
            _viewModel.AppendImportantLog($"{modeText} 시스템 로그 저장에 실패했습니다: {ex.Message}");
        }
    }

    private void ApplyRecordedVideoPlaybackSpeed()
    {
        if (RecordedVideosActiveView.PlaybackSpeedComboElement?.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            speed = 1.0;
        }

        RecordedVideosActiveView.RecordedVideoPlayerElement.SpeedRatio = speed;
    }

    private void RecordedVideoPlayer_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (RecordedVideosActiveView.RecordedVideoPlayerElement.NaturalDuration.HasTimeSpan)
        {
            var duration = RecordedVideosActiveView.RecordedVideoPlayerElement.NaturalDuration.TimeSpan;
            RecordedVideosActiveView.RecordedVideoPositionSliderElement.Maximum = Math.Max(duration.TotalSeconds, 1);
            RecordedVideosActiveView.RecordedVideoDurationTextElement.Text = FormatVideoTime(duration);
        }

        UpdateRecordedVideoPositionUi();
    }

    private void RecordedVideoPlayer_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _recordedVideoPositionTimer.Stop();
        UpdateRecordedVideoPositionUi();
    }

    private void RecordedVideoPlayer_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _recordedVideoPositionTimer.Stop();
        RecordedVideosActiveView.RecordedVideosStatusTextElement.Text = "영상을 재생할 수 없습니다.";
        _viewModel.AppendImportantLog($"녹화 영상 재생에 실패했습니다: {e.ErrorException.Message}");
    }

    private void RecordedVideoPositionTimer_OnTick(object? sender, EventArgs e)
    {
        UpdateRecordedVideoPositionUi();
    }

    private void RecordedVideoPositionSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRecordedVideoPosition = true;
    }

    private void RecordedVideoPositionSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SeekRecordedVideoToSlider();
        _isDraggingRecordedVideoPosition = false;
    }

    private void RecordedVideoPositionSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingRecordedVideoPosition)
        {
            RecordedVideosActiveView.RecordedVideoCurrentTimeTextElement.Text = FormatVideoTime(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private void SeekRecordedVideoToSlider()
    {
        RecordedVideosActiveView.RecordedVideoPlayerElement.Position = TimeSpan.FromSeconds(RecordedVideosActiveView.RecordedVideoPositionSliderElement.Value);
        UpdateRecordedVideoPositionUi();
    }

    private void UpdateRecordedVideoPositionUi()
    {
        if (_isDraggingRecordedVideoPosition)
        {
            return;
        }

        var position = RecordedVideosActiveView.RecordedVideoPlayerElement.Position;
        RecordedVideosActiveView.RecordedVideoCurrentTimeTextElement.Text = FormatVideoTime(position);

        if (RecordedVideosActiveView.RecordedVideoPlayerElement.NaturalDuration.HasTimeSpan)
        {
            var duration = RecordedVideosActiveView.RecordedVideoPlayerElement.NaturalDuration.TimeSpan;
            RecordedVideosActiveView.RecordedVideoPositionSliderElement.Maximum = Math.Max(duration.TotalSeconds, 1);
            RecordedVideosActiveView.RecordedVideoDurationTextElement.Text = FormatVideoTime(duration);
        }

        RecordedVideosActiveView.RecordedVideoPositionSliderElement.Value = Math.Min(position.TotalSeconds, RecordedVideosActiveView.RecordedVideoPositionSliderElement.Maximum);
    }

    private void ResetRecordedVideoPositionUi()
    {
        _isDraggingRecordedVideoPosition = false;
        RecordedVideosActiveView.RecordedVideoPositionSliderElement.Minimum = 0;
        RecordedVideosActiveView.RecordedVideoPositionSliderElement.Maximum = 1;
        RecordedVideosActiveView.RecordedVideoPositionSliderElement.Value = 0;
        RecordedVideosActiveView.RecordedVideoCurrentTimeTextElement.Text = "00:00";
        RecordedVideosActiveView.RecordedVideoDurationTextElement.Text = "00:00";
    }

    private void RecordedVideoViewport_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustRecordedVideoZoom(e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    private void RecordedVideoViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_recordedVideoZoomLevel <= 1.0)
        {
            return;
        }

        _isDraggingRecordedVideoPan = true;
        _lastRecordedVideoPanPoint = e.GetPosition(RecordedVideosActiveView.RecordedVideoViewportElement);
        RecordedVideosActiveView.RecordedVideoViewportElement.CaptureMouse();
        RecordedVideosActiveView.RecordedVideoViewportElement.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void RecordedVideoViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRecordedVideoPan)
        {
            return;
        }

        var point = e.GetPosition(RecordedVideosActiveView.RecordedVideoViewportElement);
        _recordedVideoPanX += point.X - _lastRecordedVideoPanPoint.X;
        _recordedVideoPanY += point.Y - _lastRecordedVideoPanPoint.Y;
        _lastRecordedVideoPanPoint = point;
        ClampRecordedVideoPan();
        UpdateRecordedVideoZoomUi();
    }

    private void RecordedVideoViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopRecordedVideoPanDrag();
    }

    private void RecordedVideoZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RecordedVideosActiveView.RecordedVideoScaleTransformElement is null)
        {
            return;
        }

        SetRecordedVideoZoom(e.NewValue);
    }

    private void RecordedVideoZoomResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ResetRecordedVideoZoom();
    }

    private void AdjustRecordedVideoZoom(double delta)
    {
        SetRecordedVideoZoom(_recordedVideoZoomLevel + delta);
    }

    private void SetRecordedVideoZoom(double value)
    {
        _recordedVideoZoomLevel = Math.Clamp(value, 1.0, 4.0);
        if (_recordedVideoZoomLevel <= 1.0)
        {
            _recordedVideoPanX = 0;
            _recordedVideoPanY = 0;
            StopRecordedVideoPanDrag();
        }

        ClampRecordedVideoPan();
        UpdateRecordedVideoZoomUi();
    }

    private void ResetRecordedVideoZoom()
    {
        _recordedVideoZoomLevel = 1.0;
        _recordedVideoPanX = 0;
        _recordedVideoPanY = 0;
        StopRecordedVideoPanDrag();
        UpdateRecordedVideoZoomUi();
    }

    private void StopRecordedVideoPanDrag()
    {
        if (!_isDraggingRecordedVideoPan)
        {
            return;
        }

        _isDraggingRecordedVideoPan = false;
        RecordedVideosActiveView.RecordedVideoViewportElement.ReleaseMouseCapture();
        RecordedVideosActiveView.RecordedVideoViewportElement.Cursor = Cursors.Arrow;
    }

    private void ClampRecordedVideoPan()
    {
        var maxPanX = GetRecordedVideoMaxPanX();
        var maxPanY = GetRecordedVideoMaxPanY();
        _recordedVideoPanX = Math.Clamp(_recordedVideoPanX, -maxPanX, maxPanX);
        _recordedVideoPanY = Math.Clamp(_recordedVideoPanY, -maxPanY, maxPanY);
    }

    private double GetRecordedVideoMaxPanX()
    {
        return Math.Max(0, RecordedVideosActiveView.RecordedVideoViewportElement.ActualWidth * (_recordedVideoZoomLevel - 1.0) / 2.0);
    }

    private double GetRecordedVideoMaxPanY()
    {
        return Math.Max(0, RecordedVideosActiveView.RecordedVideoViewportElement.ActualHeight * (_recordedVideoZoomLevel - 1.0) / 2.0);
    }

    private void UpdateRecordedVideoZoomUi()
    {
        if (RecordedVideosActiveView.RecordedVideoScaleTransformElement is null)
        {
            return;
        }

        RecordedVideosActiveView.RecordedVideoScaleTransformElement.ScaleX = _recordedVideoZoomLevel;
        RecordedVideosActiveView.RecordedVideoScaleTransformElement.ScaleY = _recordedVideoZoomLevel;
        RecordedVideosActiveView.RecordedVideoTranslateTransformElement.X = _recordedVideoPanX;
        RecordedVideosActiveView.RecordedVideoTranslateTransformElement.Y = _recordedVideoPanY;

        if (RecordedVideosActiveView.RecordedVideoZoomSliderElement is not null &&
            Math.Abs(RecordedVideosActiveView.RecordedVideoZoomSliderElement.Value - _recordedVideoZoomLevel) > 0.001)
        {
            RecordedVideosActiveView.RecordedVideoZoomSliderElement.Value = _recordedVideoZoomLevel;
        }

        if (RecordedVideosActiveView.RecordedVideoZoomResetButtonElement is not null)
        {
            RecordedVideosActiveView.RecordedVideoZoomResetButtonElement.Content = $"x{_recordedVideoZoomLevel:0.00}";
        }

        UpdateRecordedVideoMiniMap();
    }

    private void UpdateRecordedVideoMiniMap()
    {
        if (RecordedVideosActiveView.RecordedVideoZoomMiniMapElement is null || RecordedVideosActiveView.RecordedVideoMiniMapViewportElement is null)
        {
            return;
        }

        if (_recordedVideoZoomLevel <= 1.0)
        {
            RecordedVideosActiveView.RecordedVideoZoomMiniMapElement.Visibility = Visibility.Collapsed;
            return;
        }

        RecordedVideosActiveView.RecordedVideoZoomMiniMapElement.Visibility = Visibility.Visible;
        var viewportWidth = RecordedVideoMiniMapWidth / _recordedVideoZoomLevel;
        var viewportHeight = RecordedVideoMiniMapHeight / _recordedVideoZoomLevel;
        RecordedVideosActiveView.RecordedVideoMiniMapViewportElement.Width = viewportWidth;
        RecordedVideosActiveView.RecordedVideoMiniMapViewportElement.Height = viewportHeight;

        var maxPanX = GetRecordedVideoMaxPanX();
        var maxPanY = GetRecordedVideoMaxPanY();
        var left = maxPanX <= 0
            ? (RecordedVideoMiniMapWidth - viewportWidth) / 2
            : (1.0 - ((_recordedVideoPanX + maxPanX) / (maxPanX * 2.0))) * (RecordedVideoMiniMapWidth - viewportWidth);
        var top = maxPanY <= 0
            ? (RecordedVideoMiniMapHeight - viewportHeight) / 2
            : (1.0 - ((_recordedVideoPanY + maxPanY) / (maxPanY * 2.0))) * (RecordedVideoMiniMapHeight - viewportHeight);

        Canvas.SetLeft(RecordedVideosActiveView.RecordedVideoMiniMapViewportElement, left);
        Canvas.SetTop(RecordedVideosActiveView.RecordedVideoMiniMapViewportElement, top);
    }

    private static string FormatVideoTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static async Task<string> EnsureRecordedVideoCachedAsync(RecordedVideoItem item)
    {
        var cacheDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), RecordedVideoCacheFolderName);
        Directory.CreateDirectory(cacheDirectory);

        var localPath = System.IO.Path.Combine(cacheDirectory, SanitizeFileName(item.Name));
        if (File.Exists(localPath))
        {
            var localSize = new FileInfo(localPath).Length;
            if (item.SizeBytes <= 0 || localSize == item.SizeBytes)
            {
                return localPath;
            }
        }

        var bytes = await RecordedVideoHttpClient.GetByteArrayAsync(item.Url);
        await File.WriteAllBytesAsync(localPath, bytes);
        return localPath;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "recorded_video" : sanitized;
    }
}
}
