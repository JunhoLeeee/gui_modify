using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BroadcastControl.App.Services;
using BroadcastControl.App.ViewModels;
using System.Collections.Generic;
using System.IO;

namespace BroadcastControl.App;

// 파일 역할:
// MainWindow.xaml의 코드 비하인드로, 화면 컨트롤 이벤트와 외부 통신 서비스를 연결한다.
// UDP 영상/탐지/VLM/모터 상태를 받아 ViewModel에 반영하고, 사용자가 누른 버튼이나 설정 값을 서비스로 전달한다.
// 화면 상태 자체는 MainViewModel이 관리하므로, 이 파일은 UI 이벤트와 서비스 이벤트를 이어주는 연결 계층으로 보면 된다.

public partial class MainWindow : Window
{
    // MainWindow는 화면 요소와 서비스들을 연결하는 중심 계층이다.
    // 실제 UDP 파싱은 Services가 맡고, 여기서는 받은 데이터를 어떤 화면에 표시할지와 어떤 사용자 입력을 보낼지를 결정한다.
    private enum DisplayRotation
    {
        None,
        Rotate180,
        RotateLeft90
    }

    private const double SettingsDrawerClosedOffset = 320;
    private const double WindowedWidth = 1600;
    private const double WindowedHeight = 900;
    private const string RecordedVideoCacheFolderName = "LIG_DNA_GUI_recorded_videos";
    private const double RecordedVideoMiniMapWidth = 120;
    private const double RecordedVideoMiniMapHeight = 62;
    private static readonly TimeSpan MobileAlertCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JetsonConnectionHoldTime = TimeSpan.FromSeconds(10);
    private static readonly HttpClient RecordedVideoHttpClient = new();
    private readonly AppNetworkSettings _networkSettings;
    private readonly MainViewModel _viewModel;
    private readonly UdpEncodedVideoReceiverService _eoUdpCaptureService;
    private readonly UdpEncodedVideoReceiverService _irUdpCaptureService;
    private readonly UdpEncodedVideoReceiverService _detectionUdpReceiverService;
    private readonly ViewportRecordingService _viewportRecordingService;
    private readonly UdpMotorControlService _motorControlService;
    private readonly UdpMotorStatusReceiverService _motorStatusReceiverService;
    private readonly UdpVlmResultReceiverService _vlmResultReceiverService;
    private readonly MobileAlertHubService _mobileAlertHubService;
    private readonly DispatcherTimer _motorHoldTimer;
    private readonly DispatcherTimer _recordedVideoPositionTimer;
    private readonly DispatcherTimer _recordingMetadataTimer;
    private readonly DispatcherTimer _jetsonConnectionTimer;

    private bool _isDraggingZoom;
    private Point _lastZoomDragPoint;
    private bool _hasReceivedEoFrame;
    private bool _hasReceivedIrFrame;
    private bool _isFullscreenMode = true;
    private ReceivedVideoFrame? _latestEoFrame;
    private ReceivedVideoFrame? _latestIrFrame;
    private string? _lastStatusSignature;
    private readonly Dictionary<uint, ReceivedVideoFrame> _eoFrameCache = new();
    private readonly Dictionary<uint, ReceivedVideoFrame> _irFrameCache = new();
    private readonly Dictionary<uint, DetectionPacket> _eoDetectionCache = new();
    private readonly Dictionary<uint, DetectionPacket> _irDetectionCache = new();
    private bool _hasReceivedDetectionPacket;
    private bool _hasReceivedNonEmptyDetectionPacket;
    private bool _hasRenderedDetectionOverlay;
    private bool _isRenderingOverlay;
    private bool _isViewportRecordingActive;
    private bool _isDraggingRecordedVideoPosition;
    private bool _isDraggingRecordedVideoPan;
    private readonly List<RecordedVideoItem> _recordedVideoFiles = new();
    private string? _recordedVideoSelectedFolder;
    private double _recordedVideoZoomLevel = 1.0;
    private double _recordedVideoPanX;
    private double _recordedVideoPanY;
    private Point _lastRecordedVideoPanPoint;
    private string? _lastDetectionAlertSignature;
    private DateTimeOffset _lastMobileAlertAt = DateTimeOffset.MinValue;
    private DateTime _recordingMetadataWindowStart;
    private DateTime _lastJetsonMessageAt = DateTime.MinValue;
    private string? _lastFilteredOutTargetSignature;
    private string? _lastOverlaySignature;
    // VLM이 보내는 객체별 위험도를 objectId 기준으로 저장한다.
    // 바운딩 박스 색상과 시스템 위험도는 이 값을 우선 사용한다.
    private string _latestGlobalVlmThreatLevel = string.Empty;
    private readonly Dictionary<int, string> _objectThreatLevels = new();
    private readonly Dictionary<string, int> _activeMotorDirections = new(StringComparer.Ordinal);
    private readonly HashSet<Key> _pressedMotorKeys = new();
    private const int OverlayCacheLimit = 48;
    private const uint OverlayFrameTolerance = 12;
    private const float DisplayScoreThreshold = 0.60f;
    private sealed class RecordedVideoItem
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string Folder { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public bool IsFolder { get; set; }

        public bool IsBack { get; set; }
    }

    private static readonly HashSet<string> NonMilitaryTargetClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chair",
        "dining table",
        "tv",
        "laptop",
        "cell phone",
        "bottle",
        "couch",
        "bench",
        "refrigerator"
    };

    private static readonly HashSet<string> CompositeTargetClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "person",
        "airplane",
        "bicycle",
        "car",
        "motorcycle",
        "bus",
        "truck",
        "train",
        "boat",
        "cell phone",
        "laptop",
        "chair",
        "dining table",
        "tv",
        "couch",
        "bench",
        "bottle",
        "refrigerator"
    };

    public MainWindow()
    {
        InitializeComponent();

        _networkSettings = AppNetworkSettings.Load();
        _motorControlService = new UdpMotorControlService(
            _networkSettings.JetsonHost,
            _networkSettings.MotorControlPort,
            _networkSettings.TrackingRecordingControlPort);
        _viewModel = new MainViewModel(_motorControlService);
        _eoUdpCaptureService = new UdpEncodedVideoReceiverService();
        _irUdpCaptureService = new UdpEncodedVideoReceiverService(applyIrFalseColor: true);
        _detectionUdpReceiverService = new UdpEncodedVideoReceiverService();
        _viewportRecordingService = new ViewportRecordingService();
        _motorStatusReceiverService = new UdpMotorStatusReceiverService(_networkSettings.MotorStatusPort);
        _vlmResultReceiverService = new UdpVlmResultReceiverService(_networkSettings.VlmResultPort);
        _mobileAlertHubService = new MobileAlertHubService();
        _motorHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _motorHoldTimer.Tick += MotorHoldTimer_OnTick;
        _recordedVideoPositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _recordedVideoPositionTimer.Tick += RecordedVideoPositionTimer_OnTick;
        _recordingMetadataTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _recordingMetadataTimer.Tick += RecordingMetadataTimer_OnTick;
        _jetsonConnectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _jetsonConnectionTimer.Tick += JetsonConnectionTimer_OnTick;
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        PreviewKeyUp += MainWindow_OnPreviewKeyUp;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 앱 시작 시 모든 수신 서비스를 연결한다.
        // EO/IR 영상, 모터 상태, VLM 결과, 모바일 알림 서버가 각각 독립적으로 동작한다.
        WindowState = WindowState.Maximized;
        UpdateWindowModeButtonText();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ManualAnalysisSaveRequested += ViewModel_OnManualAnalysisSaveRequested;
        _viewModel.ManualSystemLogSaveRequested += ViewModel_OnManualSystemLogSaveRequested;
        _eoUdpCaptureService.FrameReady += OnEoFrameReady;
        _eoUdpCaptureService.DetectionsReceived += OnEoDetectionsReceived;
        _eoUdpCaptureService.StatusReceived += OnYoloStatusReceived;
        _irUdpCaptureService.FrameReady += OnIrFrameReady;
        _irUdpCaptureService.DetectionsReceived += OnIrDetectionsReceived;
        _irUdpCaptureService.StatusReceived += OnYoloStatusReceived;
        _detectionUdpReceiverService.DetectionsReceived += OnSharedDetectionsReceived;
        _detectionUdpReceiverService.StatusReceived += OnYoloStatusReceived;
        _motorStatusReceiverService.StatusReceived += OnMotorStatusReceived;
        _motorStatusReceiverService.ReceiverError += OnMotorStatusReceiverError;
        _vlmResultReceiverService.ResultReceived += OnVlmResultReceived;
        _vlmResultReceiverService.ReceiverError += OnVlmResultReceiverError;

        _eoUdpCaptureService.SetBrightness(_viewModel.Brightness);
        _eoUdpCaptureService.SetContrast(_viewModel.Contrast);
        _irUdpCaptureService.SetBrightness(_viewModel.Brightness);
        _irUdpCaptureService.SetContrast(_viewModel.Contrast);
        _viewModel.InitializeMotorControlState();
        _motorStatusReceiverService.Start();
        _viewModel.AppendImportantLog($"모터 상태 수신 대기 포트: {_motorStatusReceiverService.Port}");
        _vlmResultReceiverService.Start();
        _viewModel.AppendImportantLog($"VLM 결과 수신 대기 포트: {_vlmResultReceiverService.Port}");

        _viewModel.UpdateViewportSize(CameraViewport.ActualWidth, CameraViewport.ActualHeight);
        UpdateRecordingViewportState();
        RenderDetectionOverlay();
        UpdateMotorAutomationState();
        _recordingMetadataWindowStart = DateTime.Now;
        _recordingMetadataTimer.Start();
        _jetsonConnectionTimer.Start();
        UpdateJetsonConnectionState();

        LoadNetworkSettingsEditor();
        AnimateSettingsDrawer(_viewModel.IsSettingsOpen, animate: false);

        if (_eoUdpCaptureService.Start(_networkSettings.EoUdpPort))
        {
        }
        else
        {
            _viewModel.AppendImportantLog($"Failed to start the EO UDP stream receiver on port {_networkSettings.EoUdpPort}.");
        }

        if (_irUdpCaptureService.Start(_networkSettings.IrUdpPort))
        {
        }
        else
        {
            _viewModel.AppendImportantLog($"Failed to start the IR UDP stream receiver on port {_networkSettings.IrUdpPort}.");
        }

        if (_detectionUdpReceiverService.Start(_networkSettings.DetectionUdpPort))
        {
            _viewModel.AppendImportantLog($"EO/IR 탐지 결과 수신 대기 포트: {_networkSettings.DetectionUdpPort}");
        }
        else
        {
            _viewModel.AppendImportantLog($"EO/IR 탐지 결과 수신 포트 {_networkSettings.DetectionUdpPort}를 열지 못했습니다.");
        }

        if (_mobileAlertHubService.Start(_networkSettings.MobileAlertPort))
        {
            _viewModel.AppendImportantLog($"모바일 위험 알림 앱이 시작되었습니다: {_mobileAlertHubService.AccessHintUrls}");
        }
        else
        {
            _viewModel.AppendImportantLog($"모바일 위험 알림 앱 시작에 실패했습니다. 포트 {_networkSettings.MobileAlertPort}를 확인하세요.");
        }
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

    private void CameraViewport_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.UpdateViewportSize(e.NewSize.Width, e.NewSize.Height);
        UpdateRecordingViewportState();
        RenderDetectionOverlay(forceRefresh: true);
    }

    private void CameraViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 큰 영상 화면을 클릭했을 때 먼저 바운딩 박스 선택을 시도한다.
        // 박스 안을 클릭한 경우 해당 YOLO 객체 ID를 모터 추적 대상으로 보내고, 박스가 없으면 기존 줌 드래그 동작을 수행한다.
        if (TrySelectDetectionAtPoint(e.GetPosition(CameraViewport)))
        {
            e.Handled = true;
            return;
        }

        if (!_viewModel.ShowZoomMiniMap)
        {
            return;
        }

        _isDraggingZoom = true;
        _lastZoomDragPoint = e.GetPosition(CameraViewport);
        CameraViewport.CaptureMouse();
    }

    private void CameraViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingZoom)
        {
            return;
        }

        var currentPoint = e.GetPosition(CameraViewport);
        var delta = currentPoint - _lastZoomDragPoint;
        _lastZoomDragPoint = currentPoint;

        _viewModel.PanZoom(delta.X, delta.Y);
    }

    private void CameraViewport_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.CanUseZoomControls)
        {
            return;
        }

        _viewModel.AdjustZoomByWheel(e.Delta / 120.0);
        e.Handled = true;
    }

    private void CameraViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingZoom)
        {
            return;
        }

        _isDraggingZoom = false;
        CameraViewport.ReleaseMouseCapture();
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

    private void HandleDetectionsReceived(
        DetectionPacket detectionPacket,
        Dictionary<uint, DetectionPacket> detectionCache,
        bool isPrimaryCamera)
    {
        // detection은 영상 프레임보다 조금 늦게 도착할 수 있으므로 frame_id 기준으로 캐시에 보관한다.
        // 렌더링 단계에서 가장 가까운 프레임의 detection을 찾아 박스를 그린다.
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

        NotifyDetectionAlertIfNeeded(detectionPacket.FrameId, displayDetections);
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
        UpdateRiskAndMobileAlert(detectionPacket.FrameId, displayDetections);
    }

    private void RefreshPrimaryTrackingTarget()
    {
        // 모터 추적 ID는 현재 큰 화면에 표시되는 카메라의 객체만 기준으로 고른다.
        // VLM 결과가 늦게 도착해 위험도가 갱신되는 경우에도 캐시된 현재 화면 detection으로 다시 판단한다.
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

    private void OnVlmResultReceived(object? sender, VlmResultPacket result)
    {
        Dispatcher.Invoke(() =>
        {
            MarkJetsonMessageReceived();
            // VLM 결과는 전체 위험도와 객체별 위험도로 나뉜다.
            // 전체 위험도는 시스템 상태창에, 객체별 위험도는 각 바운딩 박스 색상에 반영한다.
            if (!string.IsNullOrWhiteSpace(result.ThreatLevel))
            {
                _latestGlobalVlmThreatLevel = NormalizeThreatLevel(result.ThreatLevel);
            }

            // VLM이 객체별 위험도를 보내면 track_id/objectId 기준으로 보관했다가 바운딩 박스 색과 시스템 위험도에 반영한다.
            foreach (var pair in result.ObjectThreatLevels)
            {
                _objectThreatLevels[pair.Key] = NormalizeThreatLevel(pair.Value);
            }

            var threatLevel = string.IsNullOrWhiteSpace(result.ThreatLevel)
                ? _viewModel.CurrentThreatLevel
                : result.ThreatLevel;
            var analysisMessage = string.IsNullOrWhiteSpace(result.DetectionSummary)
                ? result.AnalysisMessage
                : $"{result.AnalysisMessage} 탐지 내용: {result.DetectionSummary}";

            _viewModel.ApplyVlmAnalysisResult(threatLevel, analysisMessage);
            RefreshPrimaryTrackingTarget();
            RenderDetectionOverlay(forceRefresh: true);
        });
    }

    private void OnVlmResultReceiverError(object? sender, string message)
    {
        Dispatcher.Invoke(() => _viewModel.AppendImportantLog($"VLM 결과 수신 오류: {message}"));
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
            CameraViewport.ActualWidth,
            CameraViewport.ActualHeight);
        _irUdpCaptureService.UpdateViewportTransform(
            _viewModel.ZoomLevel,
            _viewModel.ZoomTransformX,
            _viewModel.ZoomTransformY,
            CameraViewport.ActualWidth,
            CameraViewport.ActualHeight);
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

    private void RenderDetectionOverlay(bool forceRefresh = false)
    {
        // 현재 큰 화면(EO 또는 IR)에 해당하는 최신 프레임과 detection을 맞춰 바운딩 박스를 다시 그린다.
        // 줌/창 크기/EO-IR 전환이 바뀌면 forceRefresh로 캐시된 화면 서명을 무시한다.
        if (_isRenderingOverlay)
        {
            return;
        }

        if (DetectionOverlayCanvas is null)
        {
            return;
        }

        _isRenderingOverlay = true;
        try
        {
            DetectionOverlayCanvas.Children.Clear();

            var latestFrame = _viewModel.IsEoPrimary ? _latestEoFrame : _latestIrFrame;
            if (latestFrame is null)
            {
                _lastOverlaySignature = null;
                return;
            }

            if (!TryGetRenderableFrameAndDetection(out var frameToRender, out var detectionPacket))
            {
                _lastOverlaySignature = null;
                return;
            }

            var displayDetections = ApplyThreatLevels(FilterDisplayDetections(detectionPacket.Detections));
            if (displayDetections.Count == 0)
            {
                _lastOverlaySignature = null;
                return;
            }

            var rotation = GetCurrentDisplayRotation();
            var originalSourceWidth = detectionPacket.Width > 0 ? detectionPacket.Width : frameToRender.Width;
            var originalSourceHeight = detectionPacket.Height > 0 ? detectionPacket.Height : frameToRender.Height;
            if (originalSourceWidth <= 0 || originalSourceHeight <= 0)
            {
                return;
            }

            var rotatedSourceWidth = GetRotatedWidth(originalSourceWidth, originalSourceHeight, rotation);
            var rotatedSourceHeight = GetRotatedHeight(originalSourceWidth, originalSourceHeight, rotation);
            var rotatedDetections = displayDetections
                .Select(d => RotateDetectionForDisplay(d, originalSourceWidth, originalSourceHeight, rotation))
                .ToArray();

            var overlaySignature = $"{rotation}:{BuildOverlaySignature(rotatedDetections)}";
            if (!forceRefresh && string.Equals(_lastOverlaySignature, overlaySignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastOverlaySignature = overlaySignature;

            var viewportWidth = Math.Max(CameraViewport.ActualWidth, 1);
            var viewportHeight = Math.Max(CameraViewport.ActualHeight, 1);
            var baseScale = Math.Max(viewportWidth / rotatedSourceWidth, viewportHeight / rotatedSourceHeight);
            var scaleX = baseScale;
            var scaleY = baseScale;
            var scaledWidth = rotatedSourceWidth * scaleX;
            var scaledHeight = rotatedSourceHeight * scaleY;
            var baseLeft = (viewportWidth - scaledWidth) / 2.0;
            var baseTop = (viewportHeight - scaledHeight) / 2.0;

            foreach (var detection in rotatedDetections)
            {
                var rectLeft = baseLeft + (detection.X1 * scaleX);
                var rectTop = baseTop + (detection.Y1 * scaleY);
                var rectWidth = Math.Max(2, (detection.X2 - detection.X1) * scaleX);
                var rectHeight = Math.Max(2, (detection.Y2 - detection.Y1) * scaleY);

                if (rectWidth < 2 || rectHeight < 2)
                {
                    continue;
                }

                AddDetectionVisualToCanvas(rectLeft, rectTop, rectWidth, rectHeight, detection);
            }

            if (!_hasRenderedDetectionOverlay)
            {
                _hasRenderedDetectionOverlay = true;
            }
        }
        finally
        {
            _isRenderingOverlay = false;
        }
    }

    private DisplayRotation GetCurrentDisplayRotation()
    {
        return DisplayRotation.None;
    }

    private bool TrySelectDetectionAtPoint(Point viewportPoint)
    {
        // 사용자가 누른 GUI 좌표를 현재 영상 스케일/여백/줌 이동이 적용된 바운딩 박스 좌표와 비교한다.
        // 여러 박스가 겹쳐 있으면 더 위험하고 신뢰도가 높은 객체를 우선 선택한다.
        if (!TryGetRenderableFrameAndDetection(out var frameToRender, out var detectionPacket))
        {
            return false;
        }

        var displayDetections = ApplyThreatLevels(FilterDisplayDetections(detectionPacket.Detections));
        if (displayDetections.Count == 0)
        {
            return false;
        }

        var rotation = GetCurrentDisplayRotation();
        var originalSourceWidth = detectionPacket.Width > 0 ? detectionPacket.Width : frameToRender.Width;
        var originalSourceHeight = detectionPacket.Height > 0 ? detectionPacket.Height : frameToRender.Height;
        if (originalSourceWidth <= 0 || originalSourceHeight <= 0)
        {
            return false;
        }

        var rotatedSourceWidth = GetRotatedWidth(originalSourceWidth, originalSourceHeight, rotation);
        var rotatedSourceHeight = GetRotatedHeight(originalSourceWidth, originalSourceHeight, rotation);
        var viewportWidth = Math.Max(CameraViewport.ActualWidth, 1);
        var viewportHeight = Math.Max(CameraViewport.ActualHeight, 1);
        var baseScale = Math.Max(viewportWidth / rotatedSourceWidth, viewportHeight / rotatedSourceHeight);
        var scaledWidth = rotatedSourceWidth * baseScale;
        var scaledHeight = rotatedSourceHeight * baseScale;
        var baseLeft = (viewportWidth - scaledWidth) / 2.0;
        var baseTop = (viewportHeight - scaledHeight) / 2.0;

        // 화면이 확대/이동된 상태에서도 사용자가 실제로 보는 바운딩 박스 위치를 기준으로 클릭 판정을 한다.
        var zoomLevel = Math.Max(_viewModel.ZoomLevel, 1.0);
        var viewportCenter = new Point(viewportWidth / 2.0, viewportHeight / 2.0);

        var selectedDetection = displayDetections
            .Select(detection => RotateDetectionForDisplay(detection, originalSourceWidth, originalSourceHeight, rotation))
            .Select(detection => new
            {
                Detection = detection,
                Rect = TransformRectForZoom(
                    new Rect(
                        baseLeft + (detection.X1 * baseScale),
                        baseTop + (detection.Y1 * baseScale),
                        Math.Max(2, (detection.X2 - detection.X1) * baseScale),
                        Math.Max(2, (detection.Y2 - detection.Y1) * baseScale)),
                    viewportCenter,
                    zoomLevel,
                    _viewModel.ZoomTransformX,
                    _viewModel.ZoomTransformY)
            })
            .Where(item => item.Rect.Contains(viewportPoint))
            .OrderByDescending(item => GetThreatWeight(item.Detection.ThreatLevel))
            .ThenByDescending(item => item.Detection.Score)
            .FirstOrDefault();

        if (selectedDetection is null)
        {
            return false;
        }

        _viewModel.SelectYoloObject(selectedDetection.Detection.ObjectId, selectedDetection.Detection.ThreatLevel);
        return true;
    }

    private static Rect TransformRectForZoom(Rect rect, Point center, double scale, double translateX, double translateY)
    {
        var topLeft = TransformPointForZoom(rect.TopLeft, center, scale, translateX, translateY);
        var bottomRight = TransformPointForZoom(rect.BottomRight, center, scale, translateX, translateY);
        return new Rect(topLeft, bottomRight);
    }

    private static Point TransformPointForZoom(Point point, Point center, double scale, double translateX, double translateY)
    {
        return new Point(
            center.X + ((point.X - center.X) * scale) + translateX,
            center.Y + ((point.Y - center.Y) * scale) + translateY);
    }

    private static string BuildOverlaySignature(IReadOnlyList<DetectionInfo> detections)
    {
        return string.Join(
            "|",
            detections.Select(d => $"{d.ObjectId}:{d.ClassName}:{d.ThreatLevel}:{d.X1:0}:{d.Y1:0}:{d.X2:0}:{d.Y2:0}"));
    }

    private static int GetRotatedWidth(int sourceWidth, int sourceHeight, DisplayRotation rotation)
    {
        return rotation == DisplayRotation.RotateLeft90 ? sourceHeight : sourceWidth;
    }

    private static int GetRotatedHeight(int sourceWidth, int sourceHeight, DisplayRotation rotation)
    {
        return rotation == DisplayRotation.RotateLeft90 ? sourceWidth : sourceHeight;
    }

    private static DetectionInfo RotateDetectionForDisplay(
        DetectionInfo detection,
        int sourceWidth,
        int sourceHeight,
        DisplayRotation rotation)
    {
        return rotation switch
        {
            DisplayRotation.Rotate180 => new DetectionInfo(
                detection.ClassName,
                detection.Score,
                (float)(sourceWidth - detection.X2),
                (float)(sourceHeight - detection.Y2),
                (float)(sourceWidth - detection.X1),
                (float)(sourceHeight - detection.Y1),
                detection.ObjectId,
                detection.ThreatLevel),
            DisplayRotation.RotateLeft90 => RotateDetectionLeft90(detection, sourceWidth),
            _ => detection
        };
    }

    private static DetectionInfo RotateDetectionLeft90(DetectionInfo detection, int sourceWidth)
    {
        var rotatedCorners = new[]
        {
            RotatePointLeft90(detection.X1, detection.Y1, sourceWidth),
            RotatePointLeft90(detection.X2, detection.Y1, sourceWidth),
            RotatePointLeft90(detection.X2, detection.Y2, sourceWidth),
            RotatePointLeft90(detection.X1, detection.Y2, sourceWidth)
        };

        var x1 = rotatedCorners.Min(point => point.X);
        var y1 = rotatedCorners.Min(point => point.Y);
        var x2 = rotatedCorners.Max(point => point.X);
        var y2 = rotatedCorners.Max(point => point.Y);

        return new DetectionInfo(
            detection.ClassName,
            detection.Score,
            (float)x1,
            (float)y1,
            (float)x2,
            (float)y2,
            detection.ObjectId,
            detection.ThreatLevel);
    }

    private static Point RotatePointLeft90(double x, double y, int sourceWidth)
    {
        return new Point(y, sourceWidth - x);
    }

    private static void CacheFrame(ReceivedVideoFrame frame, Dictionary<uint, ReceivedVideoFrame> frameCache)
    {
        frameCache[frame.FrameIndex] = frame;
        TrimCache(frameCache);
    }

    private static void CacheDetectionPacket(
        DetectionPacket detectionPacket,
        Dictionary<uint, DetectionPacket> detectionCache)
    {
        detectionCache[detectionPacket.FrameId] = detectionPacket;
        TrimCache(detectionCache);
    }

    private bool TryGetRenderableDetectionPacket(
        uint currentFrameId,
        Dictionary<uint, DetectionPacket> detectionCache,
        out DetectionPacket detectionPacket)
    {
        if (detectionCache.TryGetValue(currentFrameId, out detectionPacket))
        {
            return true;
        }

        var recentCandidates = detectionCache
            .Where(pair => pair.Value.Detections.Count > 0 && pair.Key <= currentFrameId)
            .OrderByDescending(pair => pair.Key)
            .ToArray();

        foreach (var candidate in recentCandidates)
        {
            var frameGap = currentFrameId >= candidate.Key
                ? currentFrameId - candidate.Key
                : uint.MaxValue;
            if (frameGap > OverlayFrameTolerance)
            {
                break;
            }

            detectionPacket = candidate.Value;
            return true;
        }

        var fallbackCandidates = detectionCache
            .Where(pair => pair.Value.Detections.Count > 0)
            .OrderByDescending(pair => pair.Key)
            .ToArray();

        foreach (var candidate in fallbackCandidates)
        {
            detectionPacket = candidate.Value;
            return true;
        }

        detectionPacket = default;
        return false;
    }

    private bool TryGetRenderableFrameAndDetection(out ReceivedVideoFrame frame, out DetectionPacket detectionPacket)
    {
        var latestFrame = _viewModel.IsEoPrimary ? _latestEoFrame : _latestIrFrame;
        var frameCache = _viewModel.IsEoPrimary ? _eoFrameCache : _irFrameCache;
        var detectionCache = _viewModel.IsEoPrimary ? _eoDetectionCache : _irDetectionCache;

        if (latestFrame is not null &&
            detectionCache.TryGetValue(latestFrame.Value.FrameIndex, out detectionPacket))
        {
            frame = latestFrame.Value;
            return true;
        }

        var exactPairs = detectionCache
            .Where(pair => pair.Value.Detections.Count > 0 && frameCache.ContainsKey(pair.Key))
            .OrderByDescending(pair => pair.Key)
            .ToArray();

        foreach (var pair in exactPairs)
        {
            frame = frameCache[pair.Key];
            detectionPacket = pair.Value;
            return true;
        }

        if (latestFrame is not null &&
            TryGetRenderableDetectionPacket(latestFrame.Value.FrameIndex, detectionCache, out detectionPacket))
        {
            frame = latestFrame.Value;
            return true;
        }

        frame = default;
        detectionPacket = default;
        return false;
    }

    private IReadOnlyList<DetectionInfo> FilterDisplayDetections(IReadOnlyList<DetectionInfo> detections)
    {
        var filtered = detections
            .Where(ShouldDisplayDetectionSafe)
            .ToArray();
        return filtered;
    }

    private IReadOnlyList<DetectionInfo> ApplyThreatLevels(IReadOnlyList<DetectionInfo> detections)
    {
        return detections
            .Select(detection => detection with { ThreatLevel = GetDetectionThreatLevel(detection) })
            .ToArray();
    }

    private static IReadOnlyList<DetectionInfo> EnsureDetectionObjectIds(IReadOnlyList<DetectionInfo> detections)
    {
        if (detections.Count == 0)
        {
            return detections;
        }

        var allIdsLookMissing = detections.All(detection => detection.ObjectId <= 0);
        var usedIds = new HashSet<int>();
        var nextDummyId = 1;
        var normalized = new DetectionInfo[detections.Count];

        for (var index = 0; index < detections.Count; index++)
        {
            var detection = detections[index];
            var objectId = detection.ObjectId;
            if (allIdsLookMissing || objectId < 0 || objectId > 254 || !usedIds.Add(objectId))
            {
                while (usedIds.Contains(nextDummyId) && nextDummyId < 255)
                {
                    nextDummyId++;
                }

                objectId = Math.Clamp(nextDummyId, 1, 254);
                usedIds.Add(objectId);
                nextDummyId++;
            }

            normalized[index] = detection with { ObjectId = objectId };
        }

        return normalized;
    }

    private string GetDetectionThreatLevel(DetectionInfo detection)
    {
        // 우선순위:
        // 1. VLM이 명시한 objectId별 위험도
        // 2. detection 자체가 가진 위험도
        // 3. VLM 전체 위험도
        // 4. 클래스 이름 기반 임시 추정값
        if (_objectThreatLevels.TryGetValue(detection.ObjectId, out var objectThreatLevel))
        {
            return NormalizeThreatLevel(objectThreatLevel);
        }

        if (!string.IsNullOrWhiteSpace(detection.ThreatLevel))
        {
            return NormalizeThreatLevel(detection.ThreatLevel);
        }

        if (_objectThreatLevels.Count == 0 && !string.IsNullOrWhiteSpace(_latestGlobalVlmThreatLevel))
        {
            return NormalizeThreatLevel(_latestGlobalVlmThreatLevel);
        }

        return EstimateThreatLevelFromClass(detection.ClassName);
    }

    private static string EstimateThreatLevelFromClass(string className)
    {
        // VLM 객체별 위험도가 아직 오지 않은 순간에도 화면 색이 완전히 비어 보이지 않도록 임시 기준을 둔다.
        var normalizedClass = className.Trim().ToLowerInvariant();
        if (normalizedClass is "airplane" or "car" or "motorcycle" or "bus" or "truck" or "train" or "boat" or "tank" or "drone" or "missile" or "weapon")
        {
            return "높음";
        }

        if (normalizedClass is "person" or "bicycle" or "cell phone" or "laptop")
        {
            return "중간";
        }

        return "낮음";
    }

    private static string NormalizeThreatLevel(string threatLevel)
    {
        return threatLevel.Trim().ToLowerInvariant() switch
        {
            "high" or "높음" => "높음",
            "medium" or "mid" or "중간" => "중간",
            _ => "낮음"
        };
    }

    private static int GetThreatWeight(string threatLevel)
    {
        return NormalizeThreatLevel(threatLevel) switch
        {
            "높음" => 3,
            "중간" => 2,
            _ => 1
        };
    }

    private static string GetHighestThreatLevel(IReadOnlyList<DetectionInfo> detections)
    {
        return detections
            .OrderByDescending(detection => GetThreatWeight(detection.ThreatLevel))
            .Select(detection => NormalizeThreatLevel(detection.ThreatLevel))
            .FirstOrDefault("낮음");
    }

    private bool ShouldDisplayDetectionSafe(DetectionInfo detection)
    {
        var className = detection.ClassName.ToLowerInvariant();
        var primaryTarget = _viewModel.SelectedPrimaryTarget;
        if (detection.Score < DisplayScoreThreshold)
        {
            return false;
        }

        if (primaryTarget == "\uBCF5\uD569")
        {
            return true;
        }

        if (primaryTarget == "\uC0AC\uB78C")
        {
            return className == "person";
        }

        if (primaryTarget == "\uACF5\uC911 \uBB34\uAE30\uCCB4\uACC4")
        {
            return className == "airplane";
        }

        if (primaryTarget == "\uC721\uC0C1 \uBB34\uAE30\uCCB4\uACC4")
        {
            return className is "bicycle" or "car" or "motorcycle" or "bus" or "truck" or "train";
        }

        if (primaryTarget == "\uD574\uC0C1 \uBB34\uAE30\uCCB4\uACC4")
        {
            return className == "boat";
        }

        if (primaryTarget == "\uD1B5\uC2E0 \uC7A5\uBE44")
        {
            return className is "cell phone" or "laptop";
        }

        if (primaryTarget == "\uBE44\uAD70\uC0AC \uD45C\uC801")
        {
            return NonMilitaryTargetClasses.Contains(className);
        }

        return true;
    }

    private bool ShouldDisplayDetection(DetectionInfo detection) => ShouldDisplayDetectionSafe(detection);

    private void NotifyDetectionAlertIfNeeded(uint frameId, IReadOnlyList<DetectionInfo> detections)
    {
        _lastDetectionAlertSignature = detections.Count == 0 ? null : $"{frameId}:{detections.Count}";
    }

    private void UpdateRiskAndMobileAlert(uint frameId, IReadOnlyList<DetectionInfo> detections)
    {
        // 시스템 위험도는 화면에 표시 중인 객체들의 위험도 중 가장 높은 값으로 결정한다.
        // 위험 상황이 반복해서 들어와도 모바일 알림이 과도하게 울리지 않도록 cooldown과 signature를 함께 사용한다.
        if (detections.Count == 0)
        {
            _viewModel.ApplyVlmAnalysisResult("낮음", "VLM 분석: 현재 선택된 주 탐지체 기준 위험 객체가 확인되지 않았습니다.");
            return;
        }

        var analysis = BuildVlmStyleAnalysis(detections);
        var detectionSummary = BuildDetectionSummary(detections);
        var systemThreatLevel = GetHighestThreatLevel(detections);
        _viewModel.ApplyVlmAnalysisResult(systemThreatLevel, $"{analysis} 탐지 내용: {detectionSummary}");

        var alertSignature = $"{_viewModel.SelectedPrimaryTarget}:{frameId}:{BuildOverlaySignature(detections)}";
        var now = DateTimeOffset.Now;
        if (string.Equals(_lastDetectionAlertSignature, alertSignature, StringComparison.Ordinal) ||
            now - _lastMobileAlertAt < MobileAlertCooldown)
        {
            return;
        }

        _lastDetectionAlertSignature = alertSignature;
        _lastMobileAlertAt = now;
        var evidencePng = CaptureElementAsPng(CameraPanel);
        _ = _mobileAlertHubService.PublishAlertAsync(
            "운용통제 위험 알림",
            analysis,
            detectionSummary,
            _viewModel.CurrentThreatLevel,
            evidencePng);
        _viewModel.AppendImportantLog("모바일 앱으로 위험 알림을 전송했습니다.");
    }

    private string BuildVlmStyleAnalysis(IReadOnlyList<DetectionInfo> detections)
    {
        return
            $"{_viewModel.LargeFeedTitle} 영상에서 주 탐지체 '{_viewModel.SelectedPrimaryTarget}' 기준 위험 객체 {detections.Count}개가 확인되었습니다. " +
            "운용자는 현 화면의 바운딩 박스 위치를 확인하고 추적/녹화 상태를 유지하십시오.";
    }

    private static string BuildDetectionSummary(IReadOnlyList<DetectionInfo> detections)
    {
        return string.Join(
            "\n",
            detections
                .OrderByDescending(d => d.Score)
                .Take(8)
                .Select((d, index) => $"{index + 1}. {d.ClassName} object{d.ObjectId} / 위험도 {d.ThreatLevel} / 신뢰도 {d.Score:0.00} / bbox ({d.X1:0}, {d.Y1:0})-({d.X2:0}, {d.Y2:0})"));
    }

    private static IReadOnlyList<DetectionTargetItem> BuildDetectionTargetItems(
        IReadOnlyList<DetectionInfo> detections,
        ReceivedVideoFrame? frame,
        int sourceWidth,
        int sourceHeight)
    {
        return detections
            .Take(30)
            .Select(detection =>
            {
                var threatBrush = GetDetectionThreatBrush(detection.ThreatLevel);
                threatBrush.Freeze();
                return new DetectionTargetItem(
                    detection.ObjectId,
                    detection.ClassName,
                    $"{detection.Score * 100.0f:0.0}%",
                    NormalizeThreatLevel(detection.ThreatLevel),
                    threatBrush,
                    TryCreateDetectionThumbnail(frame?.Bitmap, detection, sourceWidth, sourceHeight));
            })
            .ToArray();
    }

    private static ImageSource? TryCreateDetectionThumbnail(
        BitmapSource? bitmap,
        DetectionInfo detection,
        int sourceWidth,
        int sourceHeight)
    {
        if (bitmap is null || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return null;
        }

        try
        {
            var scaleX = bitmap.PixelWidth / (double)sourceWidth;
            var scaleY = bitmap.PixelHeight / (double)sourceHeight;
            var x = Math.Clamp((int)Math.Floor(detection.X1 * scaleX), 0, Math.Max(0, bitmap.PixelWidth - 1));
            var y = Math.Clamp((int)Math.Floor(detection.Y1 * scaleY), 0, Math.Max(0, bitmap.PixelHeight - 1));
            var right = Math.Clamp((int)Math.Ceiling(detection.X2 * scaleX), x + 1, bitmap.PixelWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(detection.Y2 * scaleY), y + 1, bitmap.PixelHeight);
            var rect = new Int32Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
            var cropped = new CroppedBitmap(bitmap, rect);
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? CaptureElementAsPng(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Round(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(element.ActualHeight));
        if (width < 2 || height < 2)
        {
            return null;
        }

        try
        {
            element.UpdateLayout();
            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(element);
            renderTarget.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static void TrimCache<T>(Dictionary<uint, T> cache)
    {
        while (cache.Count > OverlayCacheLimit)
        {
            var oldestKey = cache.Keys.Min();
            cache.Remove(oldestKey);
        }
    }

    private void AddDetectionVisualToCanvas(
        double rectLeft,
        double rectTop,
        double rectWidth,
        double rectHeight,
        DetectionInfo detection)
    {
        var accentBrush = GetDetectionThreatBrush(detection.ThreatLevel);
        accentBrush.Freeze();
        var mainRectangle = new Rectangle
        {
            Width = rectWidth,
            Height = rectHeight,
            Stroke = accentBrush,
            StrokeThickness = 2,
            RadiusX = 2,
            RadiusY = 2,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(mainRectangle, rectLeft);
        Canvas.SetTop(mainRectangle, rectTop);
        DetectionOverlayCanvas.Children.Add(mainRectangle);

        var cornerLength = Math.Max(12, Math.Min(rectWidth, rectHeight) * 0.18);
        AddCornerToCanvas(rectLeft, rectTop, cornerLength, true, true, accentBrush);
        AddCornerToCanvas(rectLeft + rectWidth, rectTop, cornerLength, false, true, accentBrush);
        AddCornerToCanvas(rectLeft, rectTop + rectHeight, cornerLength, true, false, accentBrush);
        AddCornerToCanvas(rectLeft + rectWidth, rectTop + rectHeight, cornerLength, false, false, accentBrush);

        var labelText = new TextBlock
        {
            Text = detection.LabelText,
            Foreground = accentBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };

        labelText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = labelText.DesiredSize.Width;
        var labelHeight = labelText.DesiredSize.Height;
        var labelLeft = Math.Max(0, Math.Min(rectLeft, Math.Max(0, CameraViewport.ActualWidth - labelWidth - 4)));
        var preferredTop = rectTop - labelHeight - 6;
        var labelTop = preferredTop >= 0 ? preferredTop : Math.Min(CameraViewport.ActualHeight - labelHeight - 4, rectTop + 6);
        Canvas.SetLeft(labelText, labelLeft);
        Canvas.SetTop(labelText, Math.Max(0, labelTop));
        DetectionOverlayCanvas.Children.Add(labelText);
    }

    private static SolidColorBrush GetDetectionThreatBrush(string threatLevel)
    {
        return NormalizeThreatLevel(threatLevel) switch
        {
            "높음" => new SolidColorBrush(Color.FromRgb(255, 107, 107)),
            "중간" => new SolidColorBrush(Color.FromRgb(255, 193, 69)),
            _ => new SolidColorBrush(Color.FromRgb(123, 216, 143))
        };
    }

    private void AddCornerToCanvas(
        double anchorX,
        double anchorY,
        double length,
        bool isLeft,
        bool isTop,
        Brush strokeBrush)
    {
        var horizontal = new Line
        {
            X1 = anchorX,
            Y1 = anchorY,
            X2 = anchorX + (isLeft ? length : -length),
            Y2 = anchorY,
            Stroke = strokeBrush,
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square
        };

        var vertical = new Line
        {
            X1 = anchorX,
            Y1 = anchorY,
            X2 = anchorX,
            Y2 = anchorY + (isTop ? length : -length),
            Stroke = strokeBrush,
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square
        };

        DetectionOverlayCanvas.Children.Add(horizontal);
        DetectionOverlayCanvas.Children.Add(vertical);
    }

    private void HandleRecordingActiveStateChanged()
    {
        if (_viewModel.IsRecordingActive)
        {
            if (_isViewportRecordingActive)
            {
                return;
            }

            var filePath = _viewportRecordingService.StartRecordingToDesktop(CameraPanel);
            _isViewportRecordingActive = true;
            _viewModel.AppendImportantLog($"Recording started: {filePath}");
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
                _viewModel.AppendImportantLog($"Video saved: {savedPath} ({_viewportRecordingService.RecordedFrameCount} frames)");
            }
            else if (!string.IsNullOrWhiteSpace(_viewportRecordingService.LastRecordingErrorMessage))
            {
                _viewModel.AppendImportantLog($"Video save failed: {_viewportRecordingService.LastRecordingErrorMessage}");
            }
            else
            {
                _viewModel.AppendImportantLog($"Video file was not created: {savedPath} ({_viewportRecordingService.RecordedFrameCount} frames)");
            }
        }
    }

    private void RotateLargeFeedButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RotateLargeFeedClockwise();
        RenderDetectionOverlay(forceRefresh: true);
        e.Handled = true;
    }

    private void RotateInsetFeedButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RotateInsetFeedClockwise();
        RenderDetectionOverlay(forceRefresh: true);
        e.Handled = true;
    }

    private void RotateAuxCameraButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OpenRecordedVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideosPanel.Visibility = Visibility.Visible;
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
        RecordedVideoPlayer.Stop();
        RecordedVideoPlayer.Source = null;
        RecordedVideosPanel.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void RecordedVideoList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordedVideoList.SelectedItem is not RecordedVideoItem item)
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
            RecordedVideoPlayer.Stop();
            RecordedVideoPlayer.Source = null;
            ResetRecordedVideoPositionUi();
            RecordedVideosStatusText.Text = $"{item.DisplayName} 내려받는 중...";

            var localPath = await EnsureRecordedVideoCachedAsync(item);
            RecordedVideoPlayer.Source = new Uri(localPath, UriKind.Absolute);
            ResetRecordedVideoZoom();
            ApplyRecordedVideoPlaybackSpeed();
            RecordedVideoPlayer.Play();
            _recordedVideoPositionTimer.Start();
            RecordedVideosStatusText.Text = item.DisplayName;
        }
        catch (Exception ex)
        {
            RecordedVideosStatusText.Text = "영상을 재생할 수 없습니다.";
            _viewModel.AppendImportantLog($"녹화 영상 재생 준비에 실패했습니다: {ex.Message}");
        }
    }

    private void RecordedVideoPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyRecordedVideoPlaybackSpeed();
        RecordedVideoPlayer.Play();
        _recordedVideoPositionTimer.Start();
    }

    private void RecordedVideoPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideoPlayer.Pause();
    }

    private void RecordedVideoStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordedVideoPlayer.Stop();
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
        RecordedVideosStatusText.Text = "목록을 불러오는 중...";

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
            RecordedVideoList.ItemsSource = null;
            RecordedVideosStatusText.Text = "목록을 불러오지 못했습니다.";
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

        RecordedVideoList.ItemsSource = folders;
        RecordedVideosStatusText.Text = folders.Count == 0
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
                DisplayName = "[상위 폴더로]",
                IsBack = true
            }
        };
        items.AddRange(videos);

        RecordedVideoList.ItemsSource = items;
        RecordedVideosStatusText.Text = $"{folder} / {videos.Count}개 영상";
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
            includeAnalysis: true,
            includeSystemLog: true);
    }

    private async void ViewModel_OnManualAnalysisSaveRequested(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        await SaveRecordingMetadataAsync(
            now,
            now,
            manual: true,
            includeAnalysis: true,
            includeSystemLog: false);
    }

    private async void ViewModel_OnManualSystemLogSaveRequested(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        await SaveRecordingMetadataAsync(
            now,
            now,
            manual: true,
            includeAnalysis: false,
            includeSystemLog: true);
    }

    private async Task SaveRecordingMetadataAsync(
        DateTime windowStart,
        DateTime windowEnd,
        bool manual,
        bool includeAnalysis,
        bool includeSystemLog)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["manual"] = manual
            };

            if (includeAnalysis)
            {
                payload["analysisText"] = _viewModel.BuildAnalysisLogSnapshot(windowStart, windowEnd, includeAll: manual);
            }

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
                var targetName = includeAnalysis ? "VLM 분석 결과" : "시스템 로그";
                _viewModel.AppendImportantLog($"{targetName}를 젝슨 영상 폴더에 C_ 파일로 저장했습니다.");
            }
        }
        catch (Exception ex)
        {
            var modeText = manual ? "수동" : "자동";
            _viewModel.AppendImportantLog($"{modeText} 로그/VLM 저장에 실패했습니다: {ex.Message}");
        }
    }

    private void ApplyRecordedVideoPlaybackSpeed()
    {
        if (PlaybackSpeedCombo?.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            speed = 1.0;
        }

        RecordedVideoPlayer.SpeedRatio = speed;
    }

    private void RecordedVideoPlayer_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (RecordedVideoPlayer.NaturalDuration.HasTimeSpan)
        {
            var duration = RecordedVideoPlayer.NaturalDuration.TimeSpan;
            RecordedVideoPositionSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
            RecordedVideoDurationText.Text = FormatVideoTime(duration);
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
        RecordedVideosStatusText.Text = "영상을 재생할 수 없습니다.";
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
            RecordedVideoCurrentTimeText.Text = FormatVideoTime(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private void SeekRecordedVideoToSlider()
    {
        RecordedVideoPlayer.Position = TimeSpan.FromSeconds(RecordedVideoPositionSlider.Value);
        UpdateRecordedVideoPositionUi();
    }

    private void UpdateRecordedVideoPositionUi()
    {
        if (_isDraggingRecordedVideoPosition)
        {
            return;
        }

        var position = RecordedVideoPlayer.Position;
        RecordedVideoCurrentTimeText.Text = FormatVideoTime(position);

        if (RecordedVideoPlayer.NaturalDuration.HasTimeSpan)
        {
            var duration = RecordedVideoPlayer.NaturalDuration.TimeSpan;
            RecordedVideoPositionSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
            RecordedVideoDurationText.Text = FormatVideoTime(duration);
        }

        RecordedVideoPositionSlider.Value = Math.Min(position.TotalSeconds, RecordedVideoPositionSlider.Maximum);
    }

    private void ResetRecordedVideoPositionUi()
    {
        _isDraggingRecordedVideoPosition = false;
        RecordedVideoPositionSlider.Minimum = 0;
        RecordedVideoPositionSlider.Maximum = 1;
        RecordedVideoPositionSlider.Value = 0;
        RecordedVideoCurrentTimeText.Text = "00:00";
        RecordedVideoDurationText.Text = "00:00";
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
        _lastRecordedVideoPanPoint = e.GetPosition(RecordedVideoViewport);
        RecordedVideoViewport.CaptureMouse();
        RecordedVideoViewport.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void RecordedVideoViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRecordedVideoPan)
        {
            return;
        }

        var point = e.GetPosition(RecordedVideoViewport);
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
        if (RecordedVideoScaleTransform is null)
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
        RecordedVideoViewport.ReleaseMouseCapture();
        RecordedVideoViewport.Cursor = Cursors.Arrow;
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
        return Math.Max(0, RecordedVideoViewport.ActualWidth * (_recordedVideoZoomLevel - 1.0) / 2.0);
    }

    private double GetRecordedVideoMaxPanY()
    {
        return Math.Max(0, RecordedVideoViewport.ActualHeight * (_recordedVideoZoomLevel - 1.0) / 2.0);
    }

    private void UpdateRecordedVideoZoomUi()
    {
        if (RecordedVideoScaleTransform is null)
        {
            return;
        }

        RecordedVideoScaleTransform.ScaleX = _recordedVideoZoomLevel;
        RecordedVideoScaleTransform.ScaleY = _recordedVideoZoomLevel;
        RecordedVideoTranslateTransform.X = _recordedVideoPanX;
        RecordedVideoTranslateTransform.Y = _recordedVideoPanY;

        if (RecordedVideoZoomSlider is not null &&
            Math.Abs(RecordedVideoZoomSlider.Value - _recordedVideoZoomLevel) > 0.001)
        {
            RecordedVideoZoomSlider.Value = _recordedVideoZoomLevel;
        }

        if (RecordedVideoZoomResetButton is not null)
        {
            RecordedVideoZoomResetButton.Content = $"x{_recordedVideoZoomLevel:0.00}";
        }

        UpdateRecordedVideoMiniMap();
    }

    private void UpdateRecordedVideoMiniMap()
    {
        if (RecordedVideoZoomMiniMap is null || RecordedVideoMiniMapViewport is null)
        {
            return;
        }

        if (_recordedVideoZoomLevel <= 1.0)
        {
            RecordedVideoZoomMiniMap.Visibility = Visibility.Collapsed;
            return;
        }

        RecordedVideoZoomMiniMap.Visibility = Visibility.Visible;
        var viewportWidth = RecordedVideoMiniMapWidth / _recordedVideoZoomLevel;
        var viewportHeight = RecordedVideoMiniMapHeight / _recordedVideoZoomLevel;
        RecordedVideoMiniMapViewport.Width = viewportWidth;
        RecordedVideoMiniMapViewport.Height = viewportHeight;

        var maxPanX = GetRecordedVideoMaxPanX();
        var maxPanY = GetRecordedVideoMaxPanY();
        var left = maxPanX <= 0
            ? (RecordedVideoMiniMapWidth - viewportWidth) / 2
            : (1.0 - ((_recordedVideoPanX + maxPanX) / (maxPanX * 2.0))) * (RecordedVideoMiniMapWidth - viewportWidth);
        var top = maxPanY <= 0
            ? (RecordedVideoMiniMapHeight - viewportHeight) / 2
            : (1.0 - ((_recordedVideoPanY + maxPanY) / (maxPanY * 2.0))) * (RecordedVideoMiniMapHeight - viewportHeight);

        Canvas.SetLeft(RecordedVideoMiniMapViewport, left);
        Canvas.SetTop(RecordedVideoMiniMapViewport, top);
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

    private void SettingsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.IsSettingsOpen = false;
        }
    }

    private void MotorDetailsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsMotorDetailsOpen)
        {
            _viewModel.IsMotorDetailsOpen = false;
            e.Handled = true;
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
    }

    private void Button_Click_1(object sender, RoutedEventArgs e)
    {
    }

    private void LoadNetworkSettingsEditor()
    {
        JetsonHostTextBox.Text = _networkSettings.JetsonHost;

        var localAddresses = AppNetworkSettings.GetLocalIpv4Addresses();
        PcGuiHostComboBox.ItemsSource = localAddresses;
        PcGuiHostComboBox.Text = _networkSettings.PcGuiHost;
        if (localAddresses.Count > 0 && !localAddresses.Contains(_networkSettings.PcGuiHost, StringComparer.Ordinal))
        {
            _viewModel.AppendImportantLog($"현재 GUI IP 후보: {string.Join(", ", localAddresses)}");
        }
    }

    private void SaveNetworkSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _networkSettings.JetsonHost = JetsonHostTextBox.Text;
        _networkSettings.PcGuiHost = PcGuiHostComboBox.Text;
        _networkSettings.RecordedVideoUrl = $"http://{_networkSettings.JetsonHost.Trim()}:{_networkSettings.RecordingHttpPort.ToString(CultureInfo.InvariantCulture)}/";
        _networkSettings.Save();
        _motorControlService.ConfigureEndpoint(
            _networkSettings.JetsonHost,
            _networkSettings.MotorControlPort,
            _networkSettings.TrackingRecordingControlPort);

        _viewModel.AppendImportantLog($"네트워크 설정을 저장하고 즉시 적용했습니다: Jetson {_networkSettings.JetsonHost}, GUI {_networkSettings.PcGuiHost}");
        MessageBox.Show(
            "네트워크 설정을 저장했습니다.\n\n모터 명령과 녹화 영상 주소는 즉시 새 Jetson IP를 사용합니다.\nGUI IP는 Jetson 브릿지의 송출 대상 설정에도 반영되어야 영상 수신 대상이 바뀝니다.",
            "Network",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void WindowModeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowMode();
    }

    private void ToggleWindowMode()
    {
        if (_isFullscreenMode)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Width = WindowedWidth;
            Height = WindowedHeight;
            Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
            _isFullscreenMode = false;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreenMode = true;
        }

        UpdateWindowModeButtonText();
    }

    private void UpdateWindowModeButtonText()
    {
        if (WindowModeToggleButton is null)
        {
            return;
        }

        WindowModeToggleButton.Content = _isFullscreenMode
            ? _viewModel.Text["WindowMode"]
            : _viewModel.Text["FullscreenMode"];
    }

    private void MotorButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        StartMotorRepeat(direction);
        e.Handled = true;
    }

    private void MotorButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        StopMotorRepeat(direction);
        e.Handled = true;
    }

    private void MotorButton_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        StopMotorRepeat(direction);
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key == Key.C)
        {
            return;
        }

        if (TryHandleMotorStepKey(e))
        {
            return;
        }

        if (!TryMapKeyToMotorDirection(e.Key, out var direction))
        {
            return;
        }

        if (_pressedMotorKeys.Add(e.Key))
        {
            StartMotorRepeat(direction);
        }

        e.Handled = true;
    }

    private bool TryHandleMotorStepKey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return false;
        }

        var delta = e.Key switch
        {
            Key.Add or Key.OemPlus => 1,
            Key.Subtract or Key.OemMinus => -1,
            _ => 0
        };

        if (delta == 0)
        {
            return false;
        }

        var isManualStepKey = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var commandParameter = isManualStepKey
            ? $"Manual:{delta}"
            : $"Auto:{delta}";

        if (_viewModel.AdjustMotorStepCommand.CanExecute(commandParameter))
        {
            _viewModel.AdjustMotorStepCommand.Execute(commandParameter);
        }

        e.Handled = true;
        return true;
    }

    private void MainWindow_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key == Key.C)
        {
            return;
        }

        if (!TryMapKeyToMotorDirection(e.Key, out var direction))
        {
            return;
        }

        _pressedMotorKeys.Remove(e.Key);
        StopMotorRepeat(direction);
        e.Handled = true;
    }

    private void StartMotorRepeat(string direction)
    {
        if (!_viewModel.IsManualMode)
        {
            return;
        }

        if (_activeMotorDirections.TryGetValue(direction, out var count))
        {
            _activeMotorDirections[direction] = count + 1;
        }
        else
        {
            _activeMotorDirections[direction] = 1;
        }

        SendActiveMotorButtons();
        UpdateMotorPadButtonVisualStates();

        if (_activeMotorDirections.Count > 0)
        {
            _motorHoldTimer.Start();
        }
    }

    private void StopMotorRepeat(string direction)
    {
        if (!_activeMotorDirections.TryGetValue(direction, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _activeMotorDirections.Remove(direction);
        }
        else
        {
            _activeMotorDirections[direction] = count - 1;
        }

        SendActiveMotorButtons();
        UpdateMotorPadButtonVisualStates();

        if (_activeMotorDirections.Count == 0)
        {
            _motorHoldTimer.Stop();
        }
    }

    private void MotorHoldTimer_OnTick(object? sender, EventArgs e)
    {
        if (_activeMotorDirections.Count == 0 || !_viewModel.IsManualMode)
        {
            _motorHoldTimer.Stop();
            return;
        }

        SendActiveMotorButtons();
    }

    private void SendActiveMotorButtons()
    {
        if (!_viewModel.IsManualMode)
        {
            return;
        }

        _viewModel.UpdateManualButtonState(GetActiveMotorButtons());
    }

    private void UpdateMotorAutomationState()
    {
        if (_viewModel.IsAutoMode)
        {
            StopManualMotorInput();
        }
    }

    private void StopManualMotorInput()
    {
        _motorHoldTimer.Stop();
        _activeMotorDirections.Clear();
        _pressedMotorKeys.Clear();
        UpdateMotorPadButtonVisualStates();
    }

    private void UpdateMotorPadButtonVisualStates()
    {
        SetMotorPadButtonActive(MotorPadLeftButton, _activeMotorDirections.ContainsKey("Left"));
        SetMotorPadButtonActive(MotorPadRightButton, _activeMotorDirections.ContainsKey("Right"));
        SetMotorPadButtonActive(MotorPadUpButton, _activeMotorDirections.ContainsKey("Up"));
        SetMotorPadButtonActive(MotorPadDownButton, _activeMotorDirections.ContainsKey("Down"));
        SetMotorPadButtonActive(MotorPadCenterButton, _activeMotorDirections.ContainsKey("Center"));
    }

    private static void SetMotorPadButtonActive(Button? button, bool isActive)
    {
        if (button is null)
        {
            return;
        }

        if (!isActive)
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            return;
        }

        button.Background = new SolidColorBrush(Color.FromRgb(0x36, 0x55, 0x64));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    }

    private static bool TryMapKeyToMotorDirection(Key key, out string direction)
    {
        direction = key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.C => "Center",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(direction);
    }

    private MotorButtonMask GetActiveMotorButtons()
    {
        if (_activeMotorDirections.Count == 0)
        {
            return MotorButtonMask.None;
        }

        if (_activeMotorDirections.ContainsKey("Center"))
        {
            return MotorButtonMask.Center;
        }

        var buttons = MotorButtonMask.None;

        if (_activeMotorDirections.ContainsKey("Left"))
        {
            buttons |= MotorButtonMask.Left;
        }

        if (_activeMotorDirections.ContainsKey("Right"))
        {
            buttons |= MotorButtonMask.Right;
        }

        if (_activeMotorDirections.ContainsKey("Up"))
        {
            buttons |= MotorButtonMask.Up;
        }

        if (_activeMotorDirections.ContainsKey("Down"))
        {
            buttons |= MotorButtonMask.Down;
        }

        return buttons;
    }

    private void AnimateSettingsDrawer(bool isOpen, bool animate)
    {
        if (!animate)
        {
            SettingsBackdrop.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsBackdrop.IsHitTestVisible = isOpen;
            SettingsBackdrop.Opacity = isOpen ? 1.0 : 0.0;

            SettingsDrawer.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsDrawer.Opacity = isOpen ? 1.0 : 0.0;
            SettingsDrawerTransform.X = isOpen ? 0 : SettingsDrawerClosedOffset;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(isOpen ? 220 : 170);
        var easing = new CubicEase
        {
            EasingMode = isOpen ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        if (isOpen)
        {
            SettingsBackdrop.Visibility = Visibility.Visible;
            SettingsBackdrop.IsHitTestVisible = true;
            SettingsDrawer.Visibility = Visibility.Visible;
        }

        var backdropAnimation = new DoubleAnimation
        {
            To = isOpen ? 1.0 : 0.0,
            Duration = duration,
            EasingFunction = easing
        };

        var drawerOpacityAnimation = new DoubleAnimation
        {
            To = isOpen ? 1.0 : 0.0,
            Duration = duration,
            EasingFunction = easing
        };

        var drawerSlideAnimation = new DoubleAnimation
        {
            To = isOpen ? 0 : SettingsDrawerClosedOffset,
            Duration = duration,
            EasingFunction = easing
        };

        if (!isOpen)
        {
            drawerSlideAnimation.Completed += (_, _) =>
            {
                SettingsBackdrop.Visibility = Visibility.Collapsed;
                SettingsBackdrop.IsHitTestVisible = false;
                SettingsDrawer.Visibility = Visibility.Collapsed;
            };
        }

        SettingsBackdrop.BeginAnimation(OpacityProperty, backdropAnimation, HandoffBehavior.SnapshotAndReplace);
        SettingsDrawer.BeginAnimation(OpacityProperty, drawerOpacityAnimation, HandoffBehavior.SnapshotAndReplace);
        SettingsDrawerTransform.BeginAnimation(TranslateTransform.XProperty, drawerSlideAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopManualMotorInput();
        _recordedVideoPositionTimer.Stop();
        _recordingMetadataTimer.Stop();
        _jetsonConnectionTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ManualAnalysisSaveRequested -= ViewModel_OnManualAnalysisSaveRequested;
        _viewModel.ManualSystemLogSaveRequested -= ViewModel_OnManualSystemLogSaveRequested;
        _eoUdpCaptureService.FrameReady -= OnEoFrameReady;
        _eoUdpCaptureService.DetectionsReceived -= OnEoDetectionsReceived;
        _eoUdpCaptureService.StatusReceived -= OnYoloStatusReceived;
        _irUdpCaptureService.FrameReady -= OnIrFrameReady;
        _irUdpCaptureService.DetectionsReceived -= OnIrDetectionsReceived;
        _irUdpCaptureService.StatusReceived -= OnYoloStatusReceived;
        _detectionUdpReceiverService.DetectionsReceived -= OnSharedDetectionsReceived;
        _detectionUdpReceiverService.StatusReceived -= OnYoloStatusReceived;
        _motorStatusReceiverService.StatusReceived -= OnMotorStatusReceived;
        _motorStatusReceiverService.ReceiverError -= OnMotorStatusReceiverError;
        _vlmResultReceiverService.ResultReceived -= OnVlmResultReceived;
        _vlmResultReceiverService.ReceiverError -= OnVlmResultReceiverError;
        _mobileAlertHubService.Dispose();
        _viewportRecordingService.Dispose();
        _eoUdpCaptureService.Dispose();
        _irUdpCaptureService.Dispose();
        _detectionUdpReceiverService.Dispose();
        _motorStatusReceiverService.Dispose();
        _vlmResultReceiverService.Dispose();
        _motorControlService.Dispose();
    }

    private void Button_Click_2(object sender, RoutedEventArgs e)
    {

    }
}

