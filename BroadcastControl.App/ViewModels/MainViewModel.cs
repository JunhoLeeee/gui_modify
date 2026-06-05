using System.Collections.ObjectModel;
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
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.Services;
using BroadcastControl.App.ViewModels.Camera;
using BroadcastControl.App.ViewModels.Monitoring;
using BroadcastControl.App.ViewModels.Motor;
using BroadcastControl.App.ViewModels.Operation;
using BroadcastControl.App.ViewModels.Recording;

namespace BroadcastControl.App.ViewModels;

// 파일 역할:
// 메인 화면 전체를 묶는 루트 ViewModel입니다.
// 기능별 세부 로직은 Camera/Motor/Monitoring/Operation/Recording 폴더의 ViewModel 파일 안에 partial로 나누어 둡니다.
// 이 파일은 공통 필드, 생성자, Command 선언, 공통 이벤트와 보조 타입을 담당합니다.

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    // 메인 화면의 크기, 색상, 상태 표시 기본값을 정의합니다.
    // 화면 바인딩에 쓰이는 기본 상수입니다.
    private const double MiniMapWidth = 130;
    private const double MiniMapHeight = 74;

    private static readonly SolidColorBrush LowThreatBrush = CreateBrush(0x7B, 0xD8, 0x8F);
    private static readonly SolidColorBrush MediumThreatBrush = CreateBrush(0xFF, 0xC1, 0x45);
    private static readonly SolidColorBrush HighThreatBrush = CreateBrush(0xFF, 0x6B, 0x6B);
    private static readonly SolidColorBrush RecordingOnBrush = CreateBrush(0x64, 0xC5, 0x9A);
    private static readonly SolidColorBrush RecordingOffBrush = CreateBrush(0x41, 0x49, 0x55);
    private static readonly SolidColorBrush RecordingTextOffBrush = CreateBrush(0x92, 0x9D, 0xAA);

    private bool _isEoPrimary = true;
    private bool _isSettingsOpen;
    private bool _isSystemPoweredOn = true;
    private string _currentMode = "\uC218\uB3D9";
    private string _selectedPrimaryTarget = "\uBCF5\uD569";
    private string _currentThreatLevel = "\uB0AE\uC74C";
    // 화면 전반에서 공유하는 상태값입니다.
    // 모드/주 탐지체/위험도는 Operation과 Monitoring 영역에서, 밝기/대비/녹화/연결은 Camera와 Recording 영역에서 사용합니다.
    private double _brightness = 50;
    private double _contrast = 50;
    private bool _isManualRecordingEnabled;
    private bool _isRecordingSuppressed;
    private bool _isAutoRecordingLatched;
    private bool _isJetsonConnected;
    private AppThemeMode _currentThemeMode;
    private double _eoDisplayRotationAngle;
    private double _irDisplayRotationAngle;
    private double _zoomLevel = 1.0;
    private double _zoomPanX;
    private double _zoomPanY;
    private double _viewportWidth = 1;
    private double _viewportHeight = 1;
    private double _motorPan;
    private double _motorTilt;
    private ushort _motorPanRaw;
    private ushort _motorTiltRaw;
    private ushort? _panMotorFeedbackRaw;
    private ushort? _tiltMotorFeedbackRaw;
    private int _autoMotorAngleSize = DefaultMotorAngleSize;
    private int _manualMotorAngleSize = DefaultMotorAngleSize;
    private double _panMotorPositionDegrees;
    private double _tiltMotorPositionDegrees;
    private bool _isMotorDetailsOpen;
    private UiLanguage _uiLanguage = UiLanguage.English;
    private string _motorTargetPanText = string.Empty;
    private string _motorTargetTiltText = string.Empty;
    private bool _hasTrackedTarget;
    private bool _isTrackingModeEnabled;
    private int _yoloObjectId = -1;
    private bool _isUserSelectedTrackId;
    private DateTime _lastAutomaticTrackingPacketSentAt = DateTime.MinValue;
    private const double MotorMinimumDegrees = -180;
    private const double MotorMaximumDegrees = 180;
    private const int MotorRawMinimum = 0;
    private const int MotorRawMaximum = 4095;
    private const double MotorRawResolution = 4096.0;
    private const int DefaultMotorAngleSize = 5;
    private const int AutomaticTrackingResendMilliseconds = 250;
    private const int VisibleLogItemLimit = 30;
    private const int StoredLogItemLimit = 100;
    private readonly List<SystemLogItem> _systemLogHistory = new();

    // EO/IR 영상이 아직 들어오지 않았을 때 표시할 마지막 프레임과 기본 플레이스홀더 이미지입니다.
    // CameraViewModel.cs의 카메라 partial 코드가 이 값을 갱신해 큰 화면/작은 화면에 바인딩합니다.
    private ImageSource? _eoFrame;
    private ImageSource? _irFrame;
    private readonly ImageSource _eoPlaceholderFrame = CreateCameraPlaceholderFrame(string.Empty, Color.FromRgb(51, 94, 160));
    private readonly ImageSource _irPlaceholderFrame = CreateCameraPlaceholderFrame(string.Empty, Color.FromRgb(192, 109, 40));

    public MainViewModel(AppNetworkSettings? networkSettings = null)
    {
        NetworkSettings = networkSettings ?? AppNetworkSettings.Load();
        Text = new LocalizedTextProvider(() => _uiLanguage);
        Camera = new CameraViewModel();
        Motor = new MotorControlViewModel(NetworkSettings);
        Operation = new OperationControlViewModel();
        Monitoring = new MonitoringViewModel();
        Recording = new RecordingViewModel();

        // 앱에 저장된 현재 테마를 읽어 설정 drawer와 테마 버튼 상태를 초기화합니다.
        if (Application.Current is App app)
        {
            _currentThemeMode = app.CurrentThemeMode;
        }

        DetectionTargets = new ObservableCollection<DetectionTargetItem>();
        SystemLogs = new ObservableCollection<SystemLogItem>();
        PanMotorStatusItems = new ObservableCollection<MotorStatusItem>(CreateDefaultMotorStatusItems());
        TiltMotorStatusItems = new ObservableCollection<MotorStatusItem>(CreateDefaultMotorStatusItems());

        AddSystemLogItem(new SystemLogItem(DateTime.Now.ToString("HH:mm:ss"), Text["SystemStarted"]));

        PrimaryTargets = new ObservableCollection<PrimaryTargetOption>(CreatePrimaryTargetOptions());

        // 전원, 모드, 주 탐지체, 카메라 밝기/대비처럼 전체 화면에서 공통으로 쓰는 명령입니다.
        TogglePowerCommand = new RelayCommand(_ => TogglePower());
        SetModeCommand = new RelayCommand(SetMode, _ => IsSystemPoweredOn);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        SelectPrimaryTargetCommand = new RelayCommand(SelectPrimaryTarget, _ => IsSystemPoweredOn);
        ResetBrightnessCommand = new RelayCommand(_ => Brightness = 50, _ => IsSystemPoweredOn);
        ResetContrastCommand = new RelayCommand(_ => Contrast = 50, _ => IsSystemPoweredOn);
        // 카메라 줌, 녹화, 테마/언어, 모터 조작처럼 기능 패널별 버튼과 연결되는 명령입니다.
        ResetZoomCommand = new RelayCommand(_ => ZoomLevel = 1.0, _ => CanUseZoomControls);
        ToggleManualRecordingCommand = new RelayCommand(_ => ToggleManualRecording(), _ => IsSystemPoweredOn);
        SetThemeCommand = new RelayCommand(SetTheme);
        SetLanguageCommand = new RelayCommand(SetLanguage);
        SaveSystemLogsCommand = new RelayCommand(_ => ManualSystemLogSaveRequested?.Invoke(this, EventArgs.Empty));
        SwapFeedsCommand = new RelayCommand(_ => SwapFeeds());
        MoveMotorCommand = new RelayCommand(MoveMotor, _ => CanUseMotorControls);
        SendMotorTargetCommand = new RelayCommand(_ => SendMotorTargetAngles(), _ => CanUseMotorTargetControls);
        AdjustMotorStepCommand = new RelayCommand(AdjustMotorStep, _ => IsSystemPoweredOn);
        ToggleTrackingModeCommand = new RelayCommand(_ => ToggleTrackingMode(), _ => IsSystemPoweredOn);
        ToggleMotorDetailsCommand = new RelayCommand(_ => IsMotorDetailsOpen = !IsMotorDetailsOpen);
        CloseMotorDetailsCommand = new RelayCommand(_ => IsMotorDetailsOpen = false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ManualSystemLogSaveRequested;

    public ObservableCollection<DetectionTargetItem> DetectionTargets { get; }

    public ObservableCollection<SystemLogItem> SystemLogs { get; }

    public ObservableCollection<MotorStatusItem> PanMotorStatusItems { get; }

    public ObservableCollection<MotorStatusItem> TiltMotorStatusItems { get; }

    public ObservableCollection<PrimaryTargetOption> PrimaryTargets { get; }

    public LocalizedTextProvider Text { get; }

    public AppNetworkSettings NetworkSettings { get; }

    public CameraViewModel Camera { get; }

    public MotorControlViewModel Motor { get; }

    public OperationControlViewModel Operation { get; }

    public MonitoringViewModel Monitoring { get; }

    public RecordingViewModel Recording { get; }

    public ICommand TogglePowerCommand { get; }

    public ICommand SetModeCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    public ICommand SelectPrimaryTargetCommand { get; }

    public ICommand ResetBrightnessCommand { get; }

    public ICommand ResetContrastCommand { get; }

    public ICommand ResetZoomCommand { get; }

    public ICommand ToggleManualRecordingCommand { get; }

    public ICommand SetThemeCommand { get; }

    public ICommand SetLanguageCommand { get; }

    public ICommand SaveSystemLogsCommand { get; }

    public ICommand SwapFeedsCommand { get; }

    public ICommand MoveMotorCommand { get; }

    public ICommand SendMotorTargetCommand { get; }

    public ICommand AdjustMotorStepCommand { get; }

    public ICommand ToggleTrackingModeCommand { get; }

    public ICommand ToggleMotorDetailsCommand { get; }

    public ICommand CloseMotorDetailsCommand { get; }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool IsSystemPoweredOn
    {
        get => _isSystemPoweredOn;
        private set
        {
            if (SetProperty(ref _isSystemPoweredOn, value))
            {
                OnPropertyChanged(nameof(IsManualMode));
                OnPropertyChanged(nameof(ManualRecordingButtonOpacity));
                OnPropertyChanged(nameof(ManualRecordingButtonText));
                OnPropertyChanged(nameof(CanSelectAutoMode));
                OnPropertyChanged(nameof(CanSelectManualMode));
                OnPropertyChanged(nameof(CanUseMotorControls));
                OnPropertyChanged(nameof(CanUseMotorTargetControls));
                OnPropertyChanged(nameof(MotorControlsOpacity));
                OnPropertyChanged(nameof(CanUseZoomControls));
                OnPropertyChanged(nameof(TrackingModeOpacity));
                OnPropertyChanged(nameof(IsAutoMode));
                OnPropertyChanged(nameof(IsRecordingActive));
                OnPropertyChanged(nameof(RecordingIndicatorBrush));
                OnPropertyChanged(nameof(RecordingTextBrush));
                OnPropertyChanged(nameof(RecordingIndicatorOpacity));
                RaiseAllCommandStates();
            }
        }
    }


    /// <summary>
    /// 필드 값이 실제로 바뀐 경우에만 값을 저장하고 PropertyChanged를 발생시킵니다.
    /// ViewModelBase를 상속하지 않는 MainViewModel에서 WPF Binding 갱신을 공통 처리하기 위해 사용합니다.
    /// </summary>
    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void DisposeFeatureServices()
    {
        Camera.DisposeServices();
        Motor.DisposeServices();
        Recording.DisposeServices();
    }
}

/// <summary>
/// 시스템 로그 한 줄을 저장하는 데이터입니다. GUI 동작, 통신 오류, 모터 명령 결과를 기록합니다.
/// </summary>
public sealed record SystemLogItem(string Time, string Message)
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

public enum MotorStepMode
{
    Auto,
    Manual
}

public enum UiLanguage
{
    English,
    Korean
}

public sealed class LocalizedTextProvider : INotifyPropertyChanged
{
    private static readonly Dictionary<string, (string English, string Korean)> Values = new()
    {
        ["PowerExit"] = ("Exit", "\uC804\uC6D0 \uC885\uB8CC"),
        ["RecordingOn"] = ("Recording", "\uC601\uC0C1 \uB179\uD654 \uC911"),
        ["RecordingStatus"] = ("Recording Status", "\uC601\uC0C1 \uB179\uD654 \uC0C1\uD0DC"),
        ["Connecting"] = ("System Connecting", "\uC2DC\uC2A4\uD15C \uC5F0\uACB0 \uC911"),
        ["Connected"] = ("System Connected", "\uC2DC\uC2A4\uD15C \uC5F0\uACB0\uB428"),
        ["Brightness"] = ("Bright", "\uBC1D\uAE30"),
        ["Contrast"] = ("Contrast", "\uB300\uC870\uBE44"),
        ["AutoMode"] = ("Scan", "\uC2A4\uCE94"),
        ["ManualMode"] = ("Manual", "\uC218\uB3D9"),
        ["StartRecording"] = ("Start Rec", "\uB179\uD654 \uC2DC\uC791"),
        ["StopRecording"] = ("Stop Rec", "\uB179\uD654 \uC885\uB8CC"),
        ["Settings"] = ("Settings", "\uC124\uC815\uCC3D"),
        ["PrimaryTargetChange"] = ("Primary Target", "\uC8FC \uD0D0\uC9C0\uCCB4 \uBCC0\uACBD"),
        ["PrimaryTarget"] = ("Target", "\uC8FC \uD0D0\uC9C0\uCCB4"),
        ["ThemeChange"] = ("Theme", "\uD14C\uB9C8 \uBCC0\uACBD"),
        ["DarkTheme"] = ("Dark", "\uC5B4\uB450\uC6B4 \uD14C\uB9C8"),
        ["LightTheme"] = ("Light", "\uBC1D\uC740 \uD14C\uB9C8"),
        ["LanguageChange"] = ("Language", "\uC5B8\uC5B4 \uBCC0\uACBD"),
        ["English"] = ("English", "\uC601\uC5B4"),
        ["Korean"] = ("Korean", "\uD55C\uAD6D\uC5B4"),
        ["ScreenMode"] = ("Screen Mode", "\uD654\uBA74 \uBAA8\uB4DC"),
        ["WindowMode"] = ("Window Mode", "\uCC3D\uBAA8\uB4DC\uB85C \uC804\uD658"),
        ["FullscreenMode"] = ("Fullscreen", "\uC804\uCCB4\uD654\uBA74\uC73C\uB85C \uC804\uD658"),
        ["NetworkSettings"] = ("Network", "\uB124\uD2B8\uC6CC\uD06C"),
        ["JetsonIp"] = ("Jetson IP", "Jetson IP"),
        ["PcIp"] = ("GUI IP", "GUI IP"),
        ["RecordedVideoUrl"] = ("Video URL", "\uB179\uD654 URL"),
        ["Details"] = ("Details", "\uC0C1\uC138"),
        ["MotorPosition"] = ("Motor Position", "\uBAA8\uD130 \uC704\uCE58"),
        ["MotorTarget"] = ("Motor Angle Setting", "\uBAA8\uD130 \uAC01\uB3C4 \uC124\uC815"),
        ["MotorSpeed"] = ("Motor Speed", "\uBAA8\uD130 \uC138\uAE30"),
        ["MotorControl"] = ("Motor Control", "\uBAA8\uD130 \uCEE8\uD2B8\uB864"),
        ["SystemStatus"] = ("System Status", "\uC2DC\uC2A4\uD15C \uD604\uD669"),
        ["AnalysisPanel"] = ("YOLO Targets", "YOLO \uD0D0\uC9C0 \uD0C0\uAC9F"),
        ["SystemLog"] = ("System Log", "\uC2DC\uC2A4\uD15C \uB85C\uADF8"),
        ["Save"] = ("Save", "\uC800\uC7A5"),
        ["RecordedVideos"] = ("Recorded Videos", "\uB179\uD654 \uC601\uC0C1 \uBCF4\uAE30"),
        ["Refresh"] = ("Refresh", "\uC0C8\uB85C\uACE0\uCE68"),
        ["Close"] = ("Close", "\uB2EB\uAE30"),
        ["SavedVideos"] = ("Saved Videos", "\uC800\uC7A5\uB41C \uC601\uC0C1"),
        ["RecordedData"] = ("Recording Data", "\uC800\uC7A5 \uC601\uC0C1 \uD655\uC778"),
        ["CamZoom"] = ("Cam ZOOM", "\uC804\uC790 ZOOM"),
        ["ZoomMiniMap"] = ("Zoom Map", "Zoom \uBBF8\uB2C8\uB9F5"),
        ["TrackingOn"] = ("Tracking", "\uCD94\uC801"),
        ["TrackingOff"] = ("Tracking Off", "\uBE44\uCD94\uC801"),
        ["ThreatLevel"] = ("Threat", "\uC704\uD5D8 \uB4F1\uAE09"),
        ["ThreatLow"] = ("Low", "\uB0AE\uC74C"),
        ["ThreatMedium"] = ("Medium", "\uC911\uAC04"),
        ["ThreatHigh"] = ("High", "\uB192\uC74C"),
        ["TargetComposite"] = ("Composite", "\uBCF5\uD569"),
        ["TargetPerson"] = ("Person", "\uC0AC\uB78C"),
        ["TargetWeapon"] = ("Weapon", "\uBB34\uAE30\uCCB4\uACC4"),
        ["TargetComm"] = ("Telecom", "\uD1B5\uC2E0 \uC7A5\uBE44"),
        ["TargetCivil"] = ("Non-military", "\uBE44\uAD70\uC0AC \uD45C\uC801"),
        ["SystemStarted"] = ("System startup started.", "\uC2DC\uC2A4\uD15C \uAC00\uB3D9\uC744 \uC2DC\uC791\uD569\uB2C8\uB2E4."),
        ["LanguageChanged"] = ("Display language changed.", "\uD45C\uC2DC \uC5B8\uC5B4\uAC00 \uBCC0\uACBD\uB418\uC5C8\uC2B5\uB2C8\uB2E4."),
    };

    private readonly Func<UiLanguage> _languageAccessor;

    public LocalizedTextProvider(Func<UiLanguage> languageAccessor)
    {
        _languageAccessor = languageAccessor;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get
        {
            if (!Values.TryGetValue(key, out var value))
            {
                return key;
            }

            return _languageAccessor() == UiLanguage.Korean ? value.Korean : value.English;
        }
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

public sealed class PrimaryTargetOption : INotifyPropertyChanged
{
    private string _displayName;

    public PrimaryTargetOption(string value, string displayName)
    {
        Value = value;
        _displayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }
}

public sealed class MotorStatusItem : INotifyPropertyChanged
{
    private string _value;

    public MotorStatusItem(string name, string value)
    {
        Name = name;
        _value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}

public sealed record DetectionTargetItem(
    int ObjectId,
    string ClassName,
    string ScoreText,
    string ThreatLevel,
    Brush ThreatBrush,
    ImageSource? Thumbnail);

