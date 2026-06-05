using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.ViewModels;
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
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.Services;
using BroadcastControl.App.ViewModels.Camera;
using BroadcastControl.App.ViewModels.Monitoring;
using BroadcastControl.App.ViewModels.Motor;
using BroadcastControl.App.ViewModels.Operation;
using BroadcastControl.App.ViewModels.Recording;

// 파일 역할:
// MotorControlView의 Pan/Tilt 위치 표시와 모터 조작 명령을 관리합니다.
// 방향키 입력, 각도 직접 입력, Motor Speed 변경, Jetson으로 보내는 11바이트 UDP 커맨드 패킷 생성 함수가 이 파일에 있습니다.

namespace BroadcastControl.App.ViewModels.Motor
{

/// <summary>
/// 모터의 현재 Pan/Tilt 각도, 목표 raw 위치, Scan/Manual 속도, 눌린 방향키 상태를 보관합니다.
/// MotorControlView의 위치 표시, 방향키 조작, Motor Speed 버튼 바인딩에 사용됩니다.
/// </summary>
public sealed class MotorControlViewModel : ViewModelBase
{
    private double _panDegrees;
    private double _tiltDegrees;
    private ushort _targetPanRaw;
    private ushort _targetTiltRaw;
    private int _scanStep = 5;
    private int _manualStep = 5;
    private MotorButtonMask _activeButtons;

    public MotorControlViewModel()
        : this(AppNetworkSettings.Load())
    {
    }

    public MotorControlViewModel(AppNetworkSettings settings)
    {
        CommandService = new UdpMotorControlService(
            settings.JetsonHost,
            settings.MotorControlPort,
            settings.TrackingRecordingControlPort);
        StatusReceiverService = new UdpMotorStatusReceiverService(settings.MotorStatusPort);
    }

    public UdpMotorControlService CommandService { get; }

    public UdpMotorStatusReceiverService StatusReceiverService { get; }

    public double PanDegrees
    {
        get => _panDegrees;
        set => SetProperty(ref _panDegrees, value);
    }

    public double TiltDegrees
    {
        get => _tiltDegrees;
        set => SetProperty(ref _tiltDegrees, value);
    }

    public ushort TargetPanRaw
    {
        get => _targetPanRaw;
        set => SetProperty(ref _targetPanRaw, value);
    }

    public ushort TargetTiltRaw
    {
        get => _targetTiltRaw;
        set => SetProperty(ref _targetTiltRaw, value);
    }

    public int ScanStep
    {
        get => _scanStep;
        set => SetProperty(ref _scanStep, Math.Clamp(value, 1, 10));
    }

    public int ManualStep
    {
        get => _manualStep;
        set => SetProperty(ref _manualStep, Math.Clamp(value, 1, 10));
    }

    public MotorButtonMask ActiveButtons
    {
        get => _activeButtons;
        set => SetProperty(ref _activeButtons, value);
    }

    public void ConfigureNetwork(AppNetworkSettings settings)
    {
        CommandService.ConfigureEndpoint(
            settings.JetsonHost,
            settings.MotorControlPort,
            settings.TrackingRecordingControlPort);
    }

    public void StartStatusReceiver(Action<string> appendLog)
    {
        StatusReceiverService.Start();
        appendLog($"모터 상태 수신 대기 포트: {StatusReceiverService.Port}");
    }

    public void DisposeServices()
    {
        CommandService.Dispose();
        StatusReceiverService.Dispose();
    }
}
}

namespace BroadcastControl.App.ViewModels
{
// MotorControlViewModel.cs 안에 둔 MainViewModel partial 영역입니다.
// 모터 기능 코드를 Motor 폴더에 모아 MainViewModel 본문이 다시 길어지지 않도록 합니다.
public sealed partial class MainViewModel
{
    public int AutoMotorAngleSize
    {
        get => _autoMotorAngleSize;
        private set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (SetProperty(ref _autoMotorAngleSize, normalized))
            {
                OnPropertyChanged(nameof(AutoMotorAngleSizeText));
            }
        }
    }

    public int ManualMotorAngleSize
    {
        get => _manualMotorAngleSize;
        private set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (SetProperty(ref _manualMotorAngleSize, normalized))
            {
                OnPropertyChanged(nameof(ManualMotorAngleSizeText));
            }
        }
    }

    public string AutoMotorAngleSizeText => AutoMotorAngleSize.ToString(CultureInfo.InvariantCulture);

    public string ManualMotorAngleSizeText => ManualMotorAngleSize.ToString(CultureInfo.InvariantCulture);

    public bool IsTrackingModeEnabled
    {
        get => _isTrackingModeEnabled;
        private set
        {
            if (SetProperty(ref _isTrackingModeEnabled, value))
            {
                OnPropertyChanged(nameof(TrackingModeText));
                OnPropertyChanged(nameof(TrackingModeOpacity));
            }
        }
    }

    public string TrackingModeText => IsTrackingModeEnabled ? Text["TrackingOn"] : Text["TrackingOff"];

    public double TrackingModeOpacity => IsSystemPoweredOn
        ? (IsTrackingModeEnabled ? 1.0 : 0.42)
        : 0.32;

    public string PanMotorPositionText => _panMotorPositionDegrees.ToString("0.0", CultureInfo.InvariantCulture);

    public string TiltMotorPositionText => _tiltMotorPositionDegrees.ToString("0.0", CultureInfo.InvariantCulture);

    public bool IsMotorDetailsOpen
    {
        get => _isMotorDetailsOpen;
        set => SetProperty(ref _isMotorDetailsOpen, value);
    }

    public string MotorPanText => $"모터 좌우: {_motorPan:0.0}°";

    public string MotorTiltText => $"모터 상하: {_motorTilt:0.0}°";

    public string MotorTargetPanText
    {
        get => _motorTargetPanText;
        set => SetProperty(ref _motorTargetPanText, value);
    }

    public string MotorTargetTiltText
    {
        get => _motorTargetTiltText;
        set => SetProperty(ref _motorTargetTiltText, value);
    }

    public void InitializeMotorControlState()
    {
        SetMotorPosition(0, 0);
        if (!TrySendMotorCommandPacket(out var modeError, syncFromFeedback: false, forcedMode: 1))
        {
            AppendImportantLog($"초기 모터 제어 패킷 전송에 실패했습니다: {modeError}");
            return;
        }
    }

    public void MoveMotorStep(string direction)
    {
        if (!TryMapDirectionToButton(direction, out var buttons))
        {
            return;
        }

        UpdateManualButtonState(buttons);
    }

    public void SetMotorPosition(double panDegrees, double tiltDegrees)
    {
        _motorPan = NormalizeMotorDegrees(panDegrees);
        _motorTilt = NormalizeMotorDegrees(tiltDegrees);
        _motorPanRaw = DegreesToDynamixelPosition(_motorPan);
        _motorTiltRaw = DegreesToDynamixelPosition(_motorTilt);
        _panMotorPositionDegrees = _motorPan;
        _tiltMotorPositionDegrees = _motorTilt;

        OnPropertyChanged(nameof(MotorPanText));
        OnPropertyChanged(nameof(MotorTiltText));
        OnPropertyChanged(nameof(PanMotorPositionText));
        OnPropertyChanged(nameof(TiltMotorPositionText));
    }

    public void UpdateMotorStatus(MotorStatusSnapshot snapshot)
    {
        // Jetson에서 받은 pan/tilt raw 위치를 GUI 표시용 각도와 상태 항목으로 변환합니다.
        // 이후 수동 방향키나 각도 입력은 GUI 추정값이 아니라 이 피드백 위치를 기준으로 이어집니다.
        UpdateMotorStatusItems(PanMotorStatusItems, snapshot.Pan);
        _panMotorFeedbackRaw = ClampMotorRaw((int)Math.Min(snapshot.Pan.PresentPosition, (uint)MotorRawMaximum));
        _panMotorPositionDegrees = DynamixelPositionToDegrees(snapshot.Pan.PresentPosition);
        _motorPanRaw = _panMotorFeedbackRaw.Value;
        _motorPan = NormalizeMotorDegrees(_panMotorPositionDegrees);
        OnPropertyChanged(nameof(PanMotorPositionText));
        OnPropertyChanged(nameof(MotorPanText));
        if (snapshot.Tilt is { } tilt)
        {
            UpdateMotorStatusItems(TiltMotorStatusItems, tilt);
            _tiltMotorFeedbackRaw = ClampMotorRaw((int)Math.Min(tilt.PresentPosition, (uint)MotorRawMaximum));
            _tiltMotorPositionDegrees = DynamixelPositionToDegrees(tilt.PresentPosition);
            _motorTiltRaw = _tiltMotorFeedbackRaw.Value;
            _motorTilt = NormalizeMotorDegrees(_tiltMotorPositionDegrees);
            OnPropertyChanged(nameof(TiltMotorPositionText));
            OnPropertyChanged(nameof(MotorTiltText));
        }
    }

    private static void UpdateMotorStatusItems(ObservableCollection<MotorStatusItem> items, MotorStatusPacket packet)
    {
        SetMotorStatusValue(items, "Motor Value", packet.PresentPosition.ToString(CultureInfo.InvariantCulture));
        SetMotorStatusValue(items, "Actual Value", $"{DynamixelPositionToDegrees(packet.PresentPosition):0.0} deg");
        SetMotorStatusValue(items, "Motor Change Value", packet.GoalPosition.ToString(CultureInfo.InvariantCulture));
        SetMotorStatusValue(items, "Actual Change Value", $"{DynamixelPositionToDegrees(packet.GoalPosition):0.0} deg");
        SetMotorStatusValue(items, "Velocity", packet.PresentVelocity.ToString(CultureInfo.InvariantCulture));
        SetMotorStatusValue(items, "Current", packet.PresentCurrentRaw.ToString(CultureInfo.InvariantCulture));
        SetMotorStatusValue(items, "PWM", packet.PresentPwm.ToString(CultureInfo.InvariantCulture));
        SetMotorStatusValue(items, "Temperature", $"{packet.PresentTemperature} C");
        SetMotorStatusValue(items, "Voltage", $"{packet.PresentInputVoltage:0.0} V");
        SetMotorStatusValue(items, "Moving", packet.Moving == 0 ? "Stop" : "Moving");
        SetMotorStatusValue(items, "Error Status", $"0x{packet.HardwareErrorStatus:X2}");
        SetMotorStatusValue(items, "Moving Status", $"0x{packet.MovingStatus:X2}");
        SetMotorStatusValue(items, "Last Update", packet.ReceivedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    public void UpdateManualButtonState(MotorButtonMask buttons)
    {
        // 수동 모드에서 방향키가 눌리거나 떼어졌을 때 현재 버튼 마스크를 모터 명령 위치에 반영합니다.
        // 자동/scan 모드에서는 사용자가 누른 방향키가 모터 명령으로 나가지 않도록 무시합니다.
        if (!IsManualMode)
        {
            return;
        }

        ApplyMotorButtonStateToCommandTarget(buttons);

        if (!TrySendMotorCommandPacket(out var modeError, buttons, syncFromFeedback: false))
        {
            AppendImportantLog($"모터 수동 제어 패킷 전송에 실패했습니다: {modeError}");
            return;
        }
    }

    private void MoveMotor(object? parameter)
    {
        if (!CanUseMotorControls || parameter is not string direction)
        {
            return;
        }

        if (!TryMapDirectionToButton(direction, out var buttons))
        {
            return;
        }

        UpdateManualButtonState(buttons);
    }

    private void SendMotorTargetAngles()
    {
        if (!CanUseMotorTargetControls)
        {
            return;
        }

        if (!double.TryParse(MotorTargetPanText, NumberStyles.Float, CultureInfo.InvariantCulture, out var panDegrees) ||
            !double.TryParse(MotorTargetTiltText, NumberStyles.Float, CultureInfo.InvariantCulture, out var tiltDegrees))
        {
            AppendImportantLog("모터 각도 입력값을 확인하세요. 예: -180, 0, 45.5, 180");
            return;
        }

        panDegrees = NormalizeMotorDegrees(panDegrees);
        tiltDegrees = NormalizeMotorDegrees(tiltDegrees);

        _motorPanRaw = DegreesToDynamixelPosition(panDegrees);
        _motorTiltRaw = DegreesToDynamixelPosition(tiltDegrees);

        if (!TrySendMotorCommandPacket(out var error, syncFromFeedback: false, forcedMode: 1))
        {
            AppendImportantLog($"모터 각도 전송에 실패했습니다: {error}");
            return;
        }

        MotorTargetPanText = string.Empty;
        MotorTargetTiltText = string.Empty;
        AppendImportantLog($"모터 각도 전송: pan {panDegrees:0.0}°, tilt {tiltDegrees:0.0}°");
    }

    private void AdjustMotorStep(object? parameter)
    {
        if (!TryParseMotorAngleParameter(parameter, out var mode, out var delta, out var resetToDefault))
        {
            return;
        }

        if (mode == MotorStepMode.Auto)
        {
            AutoMotorAngleSize = resetToDefault ? DefaultMotorAngleSize : AutoMotorAngleSize + delta;
        }
        else
        {
            ManualMotorAngleSize = resetToDefault ? DefaultMotorAngleSize : ManualMotorAngleSize + delta;
        }

        if (!TrySendMotorCommandPacket(out var error))
        {
            AppendImportantLog($"모터 속도 전송에 실패했습니다: {error}");
            return;
        }

    }

    private static bool TryParseMotorAngleParameter(
        object? parameter,
        out MotorStepMode mode,
        out int delta,
        out bool resetToDefault)
    {
        mode = MotorStepMode.Auto;
        delta = 0;
        resetToDefault = false;

        if (parameter is string text)
        {
            var parts = text.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                mode = string.Equals(parts[0], "Manual", StringComparison.OrdinalIgnoreCase)
                    ? MotorStepMode.Manual
                    : MotorStepMode.Auto;
                if (string.Equals(parts[2], "Reset", StringComparison.OrdinalIgnoreCase))
                {
                    resetToDefault = true;
                    return true;
                }

                return int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta) && delta != 0;
            }

            if (parts.Length == 2)
            {
                if (string.Equals(parts[0], "Manual", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[0], "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    mode = string.Equals(parts[0], "Manual", StringComparison.OrdinalIgnoreCase)
                        ? MotorStepMode.Manual
                        : MotorStepMode.Auto;
                    if (string.Equals(parts[1], "Reset", StringComparison.OrdinalIgnoreCase))
                    {
                        resetToDefault = true;
                        return true;
                    }

                    return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta) && delta != 0;
                }

                if (string.Equals(parts[1], "Reset", StringComparison.OrdinalIgnoreCase))
                {
                    resetToDefault = true;
                    return true;
                }

                return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta) && delta != 0;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out delta) && delta != 0;
        }

        delta = parameter switch
        {
            int intValue => intValue,
            _ => 0
        };

        return delta != 0;
    }

    private static void SetMotorStatusValue(ObservableCollection<MotorStatusItem> items, string name, string value)
    {
        var item = items.FirstOrDefault(status => status.Name == name);
        if (item is not null)
        {
            item.Value = value;
        }
    }

    private static IEnumerable<MotorStatusItem> CreateDefaultMotorStatusItems()
    {
        var names = new[]
        {
            "Motor Value",
            "Actual Value",
            "Motor Change Value",
            "Actual Change Value",
            "Velocity",
            "Current",
            "PWM",
            "Temperature",
            "Voltage",
            "Moving",
            "Error Status",
            "Moving Status",
            "Last Update"
        };

        return names.Select(name => new MotorStatusItem(name, "-")).ToArray();
    }

    private static double NormalizeMotorDegrees(double degrees)
    {
        var rounded = Math.Round(degrees, 1, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, MotorMinimumDegrees, MotorMaximumDegrees);
    }

    private static double DynamixelPositionToDegrees(uint position)
    {
        return (Math.Min(position, (uint)MotorRawMaximum) / MotorRawResolution * 360.0) + MotorMinimumDegrees;
    }

    private static ushort DegreesToDynamixelPosition(double degrees)
    {
        var normalizedDegrees = Math.Clamp(degrees, MotorMinimumDegrees, MotorMaximumDegrees) - MotorMinimumDegrees;
        var position = (int)Math.Round(normalizedDegrees / 360.0 * MotorRawResolution, MidpointRounding.AwayFromZero);
        return ClampMotorRaw(position);
    }

    private static ushort ClampMotorRaw(int position)
    {
        return (ushort)Math.Clamp(position, MotorRawMinimum, MotorRawMaximum);
    }

    private bool TrySendMotorCommandPacket(
        out string? error,
        MotorButtonMask buttons = MotorButtonMask.None,
        bool syncFromFeedback = true,
        byte? forcedMode = null)
    {
        if (syncFromFeedback)
        {
            SyncMotorRawFromFeedback();
        }

        // 현재 GUI 모드, 추적 ID, stream 선택, 방향키, pan/tilt raw 값을 11바이트 UDP 패킷으로 전송합니다.
        return Motor.CommandService.TrySendMotorCommandPacket(
            mode: forcedMode ?? (IsManualMode ? (byte)1 : (byte)0),
            tracking: IsTrackingModeEnabled ? (byte)1 : (byte)0,
            trackId: EncodeTrackId(),
            isEoPrimary: IsEoPrimary,
            btnMask: buttons,
            panPos: _motorPanRaw,
            tiltPos: _motorTiltRaw,
            scanStep: (byte)MotorSpeedToStepDelta(AutoMotorAngleSize),
            manualStep: (byte)MotorSpeedToStepDelta(ManualMotorAngleSize),
            error: out error);
    }

    private byte EncodeTrackId()
    {
        if (!ShouldSendTrackingToZybo)
        {
            return 0xFF;
        }

        if (_isUserSelectedTrackId && _yoloObjectId is >= 0 and <= 254)
        {
            return (byte)_yoloObjectId;
        }

        return _yoloObjectId is >= 0 and <= 254
            ? (byte)_yoloObjectId
            : (byte)0xFF;
    }

    private bool ShouldSendTrackingToZybo =>
        IsTrackingModeEnabled &&
        _hasTrackedTarget &&
        _yoloObjectId >= 0;

    private readonly record struct TrackingCandidate(int ObjectId, int ThreatWeight, int Order);

    private bool SyncMotorRawFromFeedback()
    {
        if (_panMotorFeedbackRaw is not { } panRaw)
        {
            return false;
        }

        _motorPanRaw = panRaw;
        _motorPan = NormalizeMotorDegrees(DynamixelPositionToDegrees(panRaw));
        if (_tiltMotorFeedbackRaw is { } tiltRaw)
        {
            _motorTiltRaw = tiltRaw;
            _motorTilt = NormalizeMotorDegrees(DynamixelPositionToDegrees(tiltRaw));
        }

        return true;
    }

    private static int GetThreatWeight(string threatLevel)
    {
        return NormalizeThreatLevel(threatLevel) switch
        {
            "\uB192\uC74C" => 3,
            "\uC911\uAC04" => 2,
            _ => 1
        };
    }

    private static bool IsHighThreatLevel(string threatLevel)
    {
        return GetThreatWeight(threatLevel) >= 3;
    }

    private static int MotorSpeedToStepDelta(int motorSpeed)
    {
        // UI에서 선택한 Motor Speed 값을 Jetson이 사용하는 step 범위 1~10으로 제한합니다.
        // 실제 각도 변화량은 Jetson 모터 제어 쪽에서 1 step당 약 0.08도로 해석합니다.
        return Math.Clamp(motorSpeed, 1, 10);
    }

    private void ApplyMotorButtonStateToCommandTarget(MotorButtonMask buttons)
    {
        if ((buttons & MotorButtonMask.Center) == MotorButtonMask.Center)
        {
            _motorPanRaw = DegreesToDynamixelPosition(0);
            _motorTiltRaw = DegreesToDynamixelPosition(0);
        }
        else
        {
            var panRaw = _panMotorFeedbackRaw ?? _motorPanRaw;
            var tiltRaw = _tiltMotorFeedbackRaw ?? _motorTiltRaw;

            if ((buttons & MotorButtonMask.Left) == MotorButtonMask.Left)
            {
                panRaw = ClampMotorRaw(panRaw - MotorSpeedToStepDelta(ManualMotorAngleSize));
            }

            if ((buttons & MotorButtonMask.Right) == MotorButtonMask.Right)
            {
                panRaw = ClampMotorRaw(panRaw + MotorSpeedToStepDelta(ManualMotorAngleSize));
            }

            if ((buttons & MotorButtonMask.Up) == MotorButtonMask.Up)
            {
                tiltRaw = ClampMotorRaw(tiltRaw + MotorSpeedToStepDelta(ManualMotorAngleSize));
            }

            if ((buttons & MotorButtonMask.Down) == MotorButtonMask.Down)
            {
                tiltRaw = ClampMotorRaw(tiltRaw - MotorSpeedToStepDelta(ManualMotorAngleSize));
            }

            _motorPanRaw = panRaw;
            _motorTiltRaw = tiltRaw;
        }

    }

    private static bool TryMapDirectionToButton(string direction, out MotorButtonMask buttons)
    {
        buttons = direction switch
        {
            "Left" => MotorButtonMask.Left,
            "Right" => MotorButtonMask.Right,
            "Up" => MotorButtonMask.Up,
            "Down" => MotorButtonMask.Down,
            "Center" => MotorButtonMask.Center,
            _ => MotorButtonMask.None
        };

        return buttons != MotorButtonMask.None;
    }
}
}
