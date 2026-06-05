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
using BroadcastControl.App.Models.Camera;
using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.Services;
using BroadcastControl.App.ViewModels;
using System.Collections.Generic;
using System.IO;

namespace BroadcastControl.App;


public partial class MainWindow : Window
{
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
    private static readonly TimeSpan JetsonConnectionHoldTime = TimeSpan.FromSeconds(10);
    private static readonly HttpClient RecordedVideoHttpClient = new();
    private readonly MainViewModel _viewModel;
    private AppNetworkSettings _networkSettings => _viewModel.NetworkSettings;
    private UdpEncodedVideoReceiverService _eoUdpCaptureService => _viewModel.Camera.EoCaptureService;
    private UdpEncodedVideoReceiverService _irUdpCaptureService => _viewModel.Camera.IrCaptureService;
    private UdpEncodedVideoReceiverService _detectionUdpReceiverService => _viewModel.Camera.DetectionReceiverService;
    private VlmUdpReceiverService _vlmReceiverService => _viewModel.Camera.VlmReceiverService;
    private ViewportRecordingService _viewportRecordingService => _viewModel.Recording.ViewportRecordingService;
    private UdpMotorControlService _motorControlService => _viewModel.Motor.CommandService;
    private UdpMotorStatusReceiverService _motorStatusReceiverService => _viewModel.Motor.StatusReceiverService;
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
    private readonly Dictionary<int, VlmResult> _vlmResults = new();
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
    private DateTime _recordingMetadataWindowStart;
    private DateTime _lastJetsonMessageAt = DateTime.MinValue;
    private string? _lastFilteredOutTargetSignature;
    private string? _lastOverlaySignature;
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

        _viewModel = new MainViewModel();
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
        WireActiveViewEvents();

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        PreviewKeyUp += MainWindow_OnPreviewKeyUp;
    }

    private void WireActiveViewEvents()
    {
        CameraActiveView.CameraViewportSizeChanged += CameraViewport_OnSizeChanged;
        CameraActiveView.CameraViewportMouseLeftButtonDown += CameraViewport_OnMouseLeftButtonDown;
        CameraActiveView.CameraViewportMouseWheel += CameraViewport_OnMouseWheel;
        CameraActiveView.CameraViewportMouseMove += CameraViewport_OnMouseMove;
        CameraActiveView.CameraViewportMouseLeftButtonUp += CameraViewport_OnMouseLeftButtonUp;
        CameraActiveView.RotateLargeFeedRequested += RotateLargeFeedButton_OnClick;
        CameraActiveView.RotateInsetFeedRequested += RotateInsetFeedButton_OnClick;
        CameraActiveView.MotorButtonPreviewMouseLeftButtonDownRequested += MotorButton_OnPreviewMouseLeftButtonDown;
        CameraActiveView.MotorButtonPreviewMouseLeftButtonUpRequested += MotorButton_OnPreviewMouseLeftButtonUp;
        CameraActiveView.MotorButtonMouseLeaveRequested += MotorButton_OnMouseLeave;

        MotorActiveView.RotateAuxCameraRequested += RotateAuxCameraButton_OnClick;
        MotorActiveView.MotorTargetEnterClicked += Button_Click_2;
        MotorActiveView.MotorButtonPreviewMouseLeftButtonDownRequested += MotorButton_OnPreviewMouseLeftButtonDown;
        MotorActiveView.MotorButtonPreviewMouseLeftButtonUpRequested += MotorButton_OnPreviewMouseLeftButtonUp;
        MotorActiveView.MotorButtonMouseLeaveRequested += MotorButton_OnMouseLeave;
        MotorDetailsActiveView.BackdropMouseLeftButtonDownRequested += MotorDetailsBackdrop_OnMouseLeftButtonDown;

        OperationActiveView.ManualModeClicked += Button_Click;
        OperationActiveView.SaveNetworkSettingsClicked += SaveNetworkSettingsButton_OnClick;

        MonitoringActiveView.OpenRecordedVideosClicked += OpenRecordedVideosButton_OnClick;
        MonitoringActiveView.SettingsClicked += Button_Click_1;
        MonitoringActiveView.DetectionTargetSelectionChanged += DetectionTargetList_OneSelctionChanged;

        RecordedVideosActiveView.RefreshRecordedVideosClicked += RefreshRecordedVideosButton_OnClick;
        RecordedVideosActiveView.CloseRecordedVideosClicked += CloseRecordedVideosButton_OnClick;
        RecordedVideosActiveView.RecordedVideoSelectionChanged += RecordedVideoList_OnSelectionChanged;
        RecordedVideosActiveView.RecordedVideoViewportMouseWheel += RecordedVideoViewport_OnMouseWheel;
        RecordedVideosActiveView.RecordedVideoViewportMouseLeftButtonDown += RecordedVideoViewport_OnMouseLeftButtonDown;
        RecordedVideosActiveView.RecordedVideoViewportMouseMove += RecordedVideoViewport_OnMouseMove;
        RecordedVideosActiveView.RecordedVideoViewportMouseLeftButtonUp += RecordedVideoViewport_OnMouseLeftButtonUp;
        RecordedVideosActiveView.RecordedVideoMediaOpened += RecordedVideoPlayer_OnMediaOpened;
        RecordedVideosActiveView.RecordedVideoMediaEnded += RecordedVideoPlayer_OnMediaEnded;
        RecordedVideosActiveView.RecordedVideoMediaFailed += (sender, args) => RecordedVideoPlayer_OnMediaFailed(sender ?? RecordedVideosActiveView, args);
        RecordedVideosActiveView.RecordedVideoPositionSliderValueChanged += RecordedVideoPositionSlider_OnValueChanged;
        RecordedVideosActiveView.RecordedVideoPositionSliderPreviewMouseLeftButtonDown += RecordedVideoPositionSlider_OnPreviewMouseLeftButtonDown;
        RecordedVideosActiveView.RecordedVideoPositionSliderPreviewMouseLeftButtonUp += RecordedVideoPositionSlider_OnPreviewMouseLeftButtonUp;
        RecordedVideosActiveView.RecordedVideoPlayClicked += RecordedVideoPlayButton_OnClick;
        RecordedVideosActiveView.RecordedVideoPauseClicked += RecordedVideoPauseButton_OnClick;
        RecordedVideosActiveView.RecordedVideoStopClicked += RecordedVideoStopButton_OnClick;
        RecordedVideosActiveView.RecordedVideoZoomResetClicked += RecordedVideoZoomResetButton_OnClick;
        RecordedVideosActiveView.RecordedVideoZoomSliderValueChanged += RecordedVideoZoomSlider_OnValueChanged;
        RecordedVideosActiveView.PlaybackSpeedSelectionChanged += PlaybackSpeedCombo_OnSelectionChanged;

        SettingsActiveView.BackdropMouseLeftButtonDownRequested += SettingsBackdrop_OnMouseLeftButtonDown;
        SettingsActiveView.WindowModeToggleClicked += WindowModeToggleButton_OnClick;
    }

    private void SettingsActiveView_Loaded(object sender, RoutedEventArgs e)
    {

    }
}
