using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BroadcastControl.App.Models.Camera;
using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.ViewModels;

// 파일 역할:
// 상단 Recording/System 상태와 YOLO 타겟 리스트의 UI 이벤트를 외부로 전달합니다.
// 녹화 목록 열기, 설정 열기, 탐지 객체 선택 이벤트를 MainWindow 처리 흐름에 연결합니다.

namespace BroadcastControl.App.Views.Monitoring
{
public partial class MonitoringView : UserControl
{
    public MonitoringView()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? OpenRecordedVideosClicked;
    public event RoutedEventHandler? SettingsClicked;
    public event SelectionChangedEventHandler? DetectionTargetSelectionChanged;

    private void OpenRecordedVideosButton_OnClick(object sender, RoutedEventArgs e) => OpenRecordedVideosClicked?.Invoke(sender, e);

    private void Button_Click_1(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke(sender, e);

    private void DetectionTargetList_OneSelctionChanged(object sender, SelectionChangedEventArgs e) => DetectionTargetSelectionChanged?.Invoke(sender, e);
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Maximized;
        UpdateWindowModeButtonText();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ManualSystemLogSaveRequested += ViewModel_OnManualSystemLogSaveRequested;
        _eoUdpCaptureService.FrameReady += OnEoFrameReady;
        _eoUdpCaptureService.DetectionsReceived += OnEoDetectionsReceived;
        _eoUdpCaptureService.StatusReceived += OnYoloStatusReceived;
        _irUdpCaptureService.FrameReady += OnIrFrameReady;
        _irUdpCaptureService.DetectionsReceived += OnIrDetectionsReceived;
        _irUdpCaptureService.StatusReceived += OnYoloStatusReceived;
        _detectionUdpReceiverService.DetectionsReceived += OnSharedDetectionsReceived;
        _detectionUdpReceiverService.StatusReceived += OnYoloStatusReceived;
        _vlmReceiverService.ResultsReceived += OnVlmResultsReceived;
        _motorStatusReceiverService.StatusReceived += OnMotorStatusReceived;
        _motorStatusReceiverService.ReceiverError += OnMotorStatusReceiverError;

        _eoUdpCaptureService.SetBrightness(_viewModel.Brightness);
        _eoUdpCaptureService.SetContrast(_viewModel.Contrast);
        _irUdpCaptureService.SetBrightness(_viewModel.Brightness);
        _irUdpCaptureService.SetContrast(_viewModel.Contrast);
        _viewModel.InitializeMotorControlState();
        _viewModel.Motor.StartStatusReceiver(_viewModel.AppendImportantLog);

        _viewModel.UpdateViewportSize(CameraActiveView.CameraViewportElement.ActualWidth, CameraActiveView.CameraViewportElement.ActualHeight);
        UpdateRecordingViewportState();
        RenderDetectionOverlay();
        UpdateMotorAutomationState();
        _recordingMetadataWindowStart = DateTime.Now;
        _recordingMetadataTimer.Start();
        _jetsonConnectionTimer.Start();
        UpdateJetsonConnectionState();

        LoadNetworkSettingsEditor();
        AnimateSettingsDrawer(_viewModel.IsSettingsOpen, animate: false);

        _viewModel.Camera.StartNetworkReceivers(_networkSettings, _viewModel.AppendImportantLog);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Brightness):
                _eoUdpCaptureService.SetBrightness(_viewModel.Brightness);
                _irUdpCaptureService.SetBrightness(_viewModel.Brightness);
                break;

            case nameof(MainViewModel.Contrast):
                _eoUdpCaptureService.SetContrast(_viewModel.Contrast);
                _irUdpCaptureService.SetContrast(_viewModel.Contrast);
                break;

            case nameof(MainViewModel.IsRecordingActive):
                HandleRecordingActiveStateChanged();
                break;

            case nameof(MainViewModel.ZoomLevel):
            case nameof(MainViewModel.ZoomTransformX):
            case nameof(MainViewModel.ZoomTransformY):
            case nameof(MainViewModel.IsEoPrimary):
            case nameof(MainViewModel.SelectedPrimaryTarget):
                UpdateRecordingViewportState();
                RefreshPrimaryTrackingTarget();
                RenderDetectionOverlay(forceRefresh: true);
                break;

            case nameof(MainViewModel.IsSettingsOpen):
                AnimateSettingsDrawer(_viewModel.IsSettingsOpen, animate: true);
                break;

            case nameof(MainViewModel.CurrentMode):
            case nameof(MainViewModel.IsSystemPoweredOn):
                UpdateMotorAutomationState();
                break;

            case nameof(MainViewModel.IsEnglishLanguage):
            case nameof(MainViewModel.IsKoreanLanguage):
                UpdateWindowModeButtonText();
                break;
        }
    }

    private void OnEoFrameReady(ReceivedVideoFrame frame)
    {
        MarkJetsonMessageReceived();
        _latestEoFrame = frame;
        CacheFrame(frame, _eoFrameCache);

        if (!_hasReceivedEoFrame)
        {
            _hasReceivedEoFrame = true;
        }

        _viewModel.UpdateEoFrame(frame.Bitmap);
    }

    private void OnEoDetectionsReceived(DetectionPacket detectionPacket)
    {
        MarkJetsonMessageReceived();
        HandleDetectionsReceived(detectionPacket, _eoDetectionCache, _viewModel.IsEoPrimary);
    }

    private void OnIrDetectionsReceived(DetectionPacket detectionPacket)
    {
        MarkJetsonMessageReceived();
        HandleDetectionsReceived(detectionPacket, _irDetectionCache, !_viewModel.IsEoPrimary);
    }

    private void OnSharedDetectionsReceived(DetectionPacket detectionPacket)
    {
        MarkJetsonMessageReceived();
        switch (detectionPacket.Stream)
        {
            case DetectionStream.Eo:
                HandleDetectionsReceived(detectionPacket, _eoDetectionCache, _viewModel.IsEoPrimary);
                break;
            case DetectionStream.Ir:
                HandleDetectionsReceived(detectionPacket, _irDetectionCache, !_viewModel.IsEoPrimary);
                break;
        }
    }

    private void DetectionTargetList_OneSelctionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is not DetectionTargetItem item)
        {
            return;
        }

        _viewModel.SelectYoloObject(item.ObjectId, item.ThreatLevel);
        listBox.SelectedItem = null;
    }
    private void HandleDetectionsReceived(
        DetectionPacket detectionPacket,
        Dictionary<uint, DetectionPacket> detectionCache,
        bool isPrimaryCamera)
    {
        CacheDetectionPacket(detectionPacket, detectionCache);

        if (!_hasReceivedDetectionPacket)
        {
            _hasReceivedDetectionPacket = true;
        }

        var displayDetections = EnsureDetectionObjectIds(
            ApplyThreatLevels(FilterDisplayDetections(detectionPacket.Detections)));

        if (!_hasReceivedNonEmptyDetectionPacket && displayDetections.Count > 0)
        {
            _hasReceivedNonEmptyDetectionPacket = true;
        }

        if (detectionPacket.Detections.Count > 0 && displayDetections.Count == 0)
        {
            var filteredSignature = $"{_viewModel.SelectedPrimaryTarget}:{detectionPacket.FrameId}";
            if (!string.Equals(_lastFilteredOutTargetSignature, filteredSignature, StringComparison.Ordinal))
            {
                _lastFilteredOutTargetSignature = filteredSignature;
            }
        }
        else
        {
            _lastFilteredOutTargetSignature = null;
        }

        if (isPrimaryCamera)
        {
            _viewModel.UpdateDetectionSummary(displayDetections);
            _viewModel.UpdateDetectionTargets(BuildDetectionTargetItems(
                displayDetections,
                _viewModel.IsEoPrimary ? _latestEoFrame : _latestIrFrame,
                detectionPacket.Width,
                detectionPacket.Height));
        }

        RenderDetectionOverlay(forceRefresh: true);
    }

    private void RefreshPrimaryTrackingTarget()
    {
        if (!TryGetRenderableFrameAndDetection(out var frame, out var detectionPacket))
        {
            _viewModel.UpdateDetectionSummary(Array.Empty<DetectionInfo>());
            _viewModel.UpdateDetectionTargets(Array.Empty<DetectionTargetItem>());
            return;
        }

        var displayDetections = EnsureDetectionObjectIds(
            ApplyThreatLevels(FilterDisplayDetections(detectionPacket.Detections)));
        _viewModel.UpdateDetectionSummary(displayDetections);
        _viewModel.UpdateDetectionTargets(BuildDetectionTargetItems(
            displayDetections,
            frame,
            detectionPacket.Width,
            detectionPacket.Height));
    }

    private void OnYoloStatusReceived(YoloStatusPacket statusPacket)
    {
        MarkJetsonMessageReceived();
        var signature = $"{statusPacket.Enabled}:{statusPacket.ModelLoaded}:{statusPacket.ConfThreshold}:{statusPacket.LastError}:{statusPacket.Source}";
        if (string.Equals(_lastStatusSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatusSignature = signature;

        if (!statusPacket.ModelLoaded)
        {
            _viewModel.AppendImportantLog("YOLO 모델이 아직 로드되지 않았습니다.");
        }

        if (!string.IsNullOrWhiteSpace(statusPacket.LastError))
        {
            _viewModel.AppendImportantLog($"YOLO 상태 오류: {statusPacket.LastError}");
        }
    }

    private void OnVlmResultsReceived(IReadOnlyList<VlmResult> results)
    {
        foreach (var r in results)
        {
            _vlmResults[r.TrackId] = r;
        }

        RenderDetectionOverlay(forceRefresh: true);
    }

    private void OnMotorStatusReceived(object? sender, MotorStatusSnapshot snapshot)
    {
        Dispatcher.Invoke(() =>
        {
            MarkJetsonMessageReceived();
            _viewModel.UpdateMotorStatus(snapshot);
        });
    }

    private void OnMotorStatusReceiverError(object? sender, string message)
    {
        Dispatcher.Invoke(() => _viewModel.AppendImportantLog($"모터 상태 수신 오류: {message}"));
    }

    private void OnIrFrameReady(ReceivedVideoFrame frame)
    {
        MarkJetsonMessageReceived();
        _latestIrFrame = frame;
        CacheFrame(frame, _irFrameCache);

        if (!_hasReceivedIrFrame)
        {
            _hasReceivedIrFrame = true;
        }

        _viewModel.UpdateIrFrame(frame.Bitmap);
    }

    private void OnEoSegmentChanged(PlaybackSegmentInfo segmentInfo)
    {
    }

    private void OnEoSegmentLoopRestarted(PlaybackSegmentInfo segmentInfo)
    {
    }

    private void OnEoDiagnosticsMessageReady(string message)
    {
    }

    private void UpdateRecordingViewportState()
    {
        _eoUdpCaptureService.UpdateViewportTransform(
            _viewModel.ZoomLevel,
            _viewModel.ZoomTransformX,
            _viewModel.ZoomTransformY,
            CameraActiveView.CameraViewportElement.ActualWidth,
            CameraActiveView.CameraViewportElement.ActualHeight);
        _irUdpCaptureService.UpdateViewportTransform(
            _viewModel.ZoomLevel,
            _viewModel.ZoomTransformX,
            _viewModel.ZoomTransformY,
            CameraActiveView.CameraViewportElement.ActualWidth,
            CameraActiveView.CameraViewportElement.ActualHeight);
    }

    private void MarkJetsonMessageReceived()
    {
        _lastJetsonMessageAt = DateTime.Now;
        if (Dispatcher.CheckAccess())
        {
            UpdateJetsonConnectionState();
        }
        else
        {
            Dispatcher.BeginInvoke(UpdateJetsonConnectionState);
        }
    }

    private void JetsonConnectionTimer_OnTick(object? sender, EventArgs e)
    {
        UpdateJetsonConnectionState();
    }

    private void UpdateJetsonConnectionState()
    {
        var isConnected =
            _lastJetsonMessageAt != DateTime.MinValue &&
            DateTime.Now - _lastJetsonMessageAt <= JetsonConnectionHoldTime;
        _viewModel.UpdateJetsonConnectionState(isConnected);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopManualMotorInput();
        _recordedVideoPositionTimer.Stop();
        _recordingMetadataTimer.Stop();
        _jetsonConnectionTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ManualSystemLogSaveRequested -= ViewModel_OnManualSystemLogSaveRequested;
        _eoUdpCaptureService.FrameReady -= OnEoFrameReady;
        _eoUdpCaptureService.DetectionsReceived -= OnEoDetectionsReceived;
        _eoUdpCaptureService.StatusReceived -= OnYoloStatusReceived;
        _irUdpCaptureService.FrameReady -= OnIrFrameReady;
        _irUdpCaptureService.DetectionsReceived -= OnIrDetectionsReceived;
        _irUdpCaptureService.StatusReceived -= OnYoloStatusReceived;
        _detectionUdpReceiverService.DetectionsReceived -= OnSharedDetectionsReceived;
        _detectionUdpReceiverService.StatusReceived -= OnYoloStatusReceived;
        _vlmReceiverService.ResultsReceived -= OnVlmResultsReceived;
        _motorStatusReceiverService.StatusReceived -= OnMotorStatusReceived;
        _motorStatusReceiverService.ReceiverError -= OnMotorStatusReceiverError;
        _viewModel.DisposeFeatureServices();
    }
}
}
