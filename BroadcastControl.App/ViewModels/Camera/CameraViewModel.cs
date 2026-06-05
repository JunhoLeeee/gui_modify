using System.Windows.Media;
using BroadcastControl.App.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
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

// 파일 역할:
// CameraView에서 표시하는 EO/IR 영상 프레임과 영상 조작 상태를 관리합니다.
// 큰 화면/보조 화면 전환, 마우스 휠 줌, 드래그 팬, 회전 버튼, 밝기/대비 슬라이더가 이 파일의 속성과 함수에 연결됩니다.

namespace BroadcastControl.App.ViewModels.Camera
{

/// <summary>
/// EO/IR 수신 프레임, 줌 배율, 밝기, 대비 값을 보관합니다.
/// CameraView의 Image와 슬라이더 바인딩에서 직접 읽고 갱신하는 카메라 상태입니다.
/// </summary>
public sealed class CameraViewModel : ViewModelBase
{
    private ImageSource? _eoFrame;
    private ImageSource? _irFrame;
    private double _zoomLevel = 1.0;
    private double _brightness = 50;
    private double _contrast = 50;

    public UdpEncodedVideoReceiverService EoCaptureService { get; } = new();

    public UdpEncodedVideoReceiverService IrCaptureService { get; } = new(applyIrFalseColor: true);

    public UdpEncodedVideoReceiverService DetectionReceiverService { get; } = new();

    public VlmUdpReceiverService VlmReceiverService { get; } = new();

    public ImageSource? EoFrame
    {
        get => _eoFrame;
        set => SetProperty(ref _eoFrame, value);
    }

    public ImageSource? IrFrame
    {
        get => _irFrame;
        set => SetProperty(ref _irFrame, value);
    }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set => SetProperty(ref _zoomLevel, value);
    }

    public double Brightness
    {
        get => _brightness;
        set => SetProperty(ref _brightness, value);
    }

    public double Contrast
    {
        get => _contrast;
        set => SetProperty(ref _contrast, value);
    }

    public bool StartNetworkReceivers(AppNetworkSettings settings, Action<string> appendLog)
    {
        EoCaptureService.SetBrightness(Brightness);
        EoCaptureService.SetContrast(Contrast);
        IrCaptureService.SetBrightness(Brightness);
        IrCaptureService.SetContrast(Contrast);

        var started = true;
        if (!EoCaptureService.Start(settings.EoUdpPort))
        {
            appendLog($"EO 영상 수신 포트 {settings.EoUdpPort}를 열지 못했습니다.");
            started = false;
        }

        if (!IrCaptureService.Start(settings.IrUdpPort))
        {
            appendLog($"IR 영상 수신 포트 {settings.IrUdpPort}를 열지 못했습니다.");
            started = false;
        }

        if (DetectionReceiverService.Start(settings.DetectionUdpPort))
        {
            appendLog($"EO/IR 탐지 결과 수신 대기 포트: {settings.DetectionUdpPort}");
        }
        else
        {
            appendLog($"EO/IR 탐지 결과 수신 포트 {settings.DetectionUdpPort}를 열지 못했습니다.");
            started = false;
        }

        if (settings.VlmUdpPort > 0)
        {
            if (VlmReceiverService.Start(settings.VlmUdpPort))
            {
                appendLog($"VLM 추론 결과 수신 대기 포트: {settings.VlmUdpPort}");
            }
            else
            {
                appendLog($"VLM 추론 결과 수신 포트 {settings.VlmUdpPort}를 열지 못했습니다.");
            }
        }

        return started;
    }

    public void DisposeServices()
    {
        EoCaptureService.Dispose();
        IrCaptureService.Dispose();
        DetectionReceiverService.Dispose();
        VlmReceiverService.Dispose();
    }
}
}

namespace BroadcastControl.App.ViewModels
{
// CameraViewModel.cs 안에 둔 MainViewModel partial 영역입니다.
// 기존 XAML 바인딩을 유지하면서 카메라 기능 관련 코드를 기능 폴더로 모으기 위한 구조입니다.
public sealed partial class MainViewModel
{
    public string EoTitle => "EO cam";

    public string IrTitle => "IR cam";

    public string EoSubtitle => "Jetson YOLO EO stream";

    public string IrSubtitle => "ZYBO10 -> Jetson YOLO IR stream";

    public ImageSource? LargeFeedImage => _isEoPrimary
        ? _eoFrame ?? _eoPlaceholderFrame
        : _irFrame ?? _irPlaceholderFrame;

    public ImageSource? InsetFeedImage => _isEoPrimary
        ? _irFrame ?? _irPlaceholderFrame
        : _eoFrame ?? _eoPlaceholderFrame;

    public string LargeFeedTitle => _isEoPrimary ? EoTitle : IrTitle;

    public string InsetFeedTitle => _isEoPrimary ? IrTitle : EoTitle;

    public double LargeFeedRotationAngle => _isEoPrimary ? _eoDisplayRotationAngle : _irDisplayRotationAngle;

    public double InsetFeedRotationAngle => _isEoPrimary ? _irDisplayRotationAngle : _eoDisplayRotationAngle;

    public Stretch LargeFeedStretch => HasLargeFeedFrame ? Stretch.Uniform : Stretch.UniformToFill;

    public Stretch InsetFeedStretch => HasInsetFeedFrame ? Stretch.Uniform : Stretch.UniformToFill;

    private bool HasLargeFeedFrame => _isEoPrimary ? _eoFrame is not null : _irFrame is not null;

    private bool HasInsetFeedFrame => _isEoPrimary ? _irFrame is not null : _eoFrame is not null;

    public string LargeFeedSubtitle => _isEoPrimary ? EoSubtitle : IrSubtitle;

    public string InsetFeedSubtitle => _isEoPrimary ? IrSubtitle : EoSubtitle;

    public double Brightness
    {
        get => _brightness;
        set
        {
            // 밝기 슬라이더 값이 바뀌면 표시용 텍스트도 함께 갱신합니다.
            if (SetProperty(ref _brightness, value))
            {
                OnPropertyChanged(nameof(BrightnessText));
            }
        }
    }

    public string BrightnessText => $"{Text["Brightness"]} {Brightness:0}%";

    public double Contrast
    {
        get => _contrast;
        set
        {
            // 대비 슬라이더 값이 바뀌면 표시용 텍스트도 함께 갱신합니다.
            if (SetProperty(ref _contrast, value))
            {
                OnPropertyChanged(nameof(ContrastText));
            }
        }
    }

    public string ContrastText => $"{Text["Contrast"]} {Contrast:0}%";

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            // 전자 줌은 1배부터 4배까지만 허용해 화면이 과도하게 확대되지 않도록 합니다.
            var clamped = Math.Clamp(value, 1.0, 4.0);
            if (SetProperty(ref _zoomLevel, clamped))
            {
                // 1배로 돌아오면 팬 위치를 원점으로 되돌려 다음 확대가 중앙에서 시작되게 합니다.
                if (_zoomLevel <= 1.0)
                {
                    _zoomPanX = 0;
                    _zoomPanY = 0;
                    OnPropertyChanged(nameof(ZoomTransformX));
                    OnPropertyChanged(nameof(ZoomTransformY));
                }

                OnPropertyChanged(nameof(ZoomLevelText));
                OnPropertyChanged(nameof(LargeFeedScale));
                OnPropertyChanged(nameof(ShowZoomMiniMap));
                UpdateMiniMapViewport();
            }
        }
    }

    public string ZoomLevelText => $"x{ZoomLevel:0.00}";

    public double LargeFeedScale => ZoomLevel;

    public double ZoomTransformX => _zoomPanX;

    public double ZoomTransformY => _zoomPanY;

    public bool ShowZoomMiniMap => IsSystemPoweredOn && ZoomLevel > 1.0;

    public double MiniMapViewportWidth => MiniMapWidth / ZoomLevel;

    public double MiniMapViewportHeight => MiniMapHeight / ZoomLevel;

    public double MiniMapViewportLeft
    {
        get
        {
            var maxPan = GetMaxPanX();
            if (maxPan <= 0)
            {
                return (MiniMapWidth - MiniMapViewportWidth) / 2;
            }

            var normalized = (_zoomPanX + maxPan) / (maxPan * 2);
            // WPF 좌표계는 왼쪽이 0이므로 pan 값을 미니맵 왼쪽 좌표로 변환합니다.
            return (1.0 - normalized) * (MiniMapWidth - MiniMapViewportWidth);
        }
    }

    public double MiniMapViewportTop
    {
        get
        {
            var maxPan = GetMaxPanY();
            if (maxPan <= 0)
            {
                return (MiniMapHeight - MiniMapViewportHeight) / 2;
            }

            var normalized = (_zoomPanY + maxPan) / (maxPan * 2);
            // WPF 좌표계는 위쪽이 0이므로 pan 값을 미니맵 위쪽 좌표로 변환합니다.
            return (1.0 - normalized) * (MiniMapHeight - MiniMapViewportHeight);
        }
    }

    public void UpdateEoFrame(ImageSource? frame)
    {
        _eoFrame = frame;
        OnPropertyChanged(nameof(LargeFeedImage));
        OnPropertyChanged(nameof(InsetFeedImage));
        OnPropertyChanged(nameof(LargeFeedStretch));
        OnPropertyChanged(nameof(InsetFeedStretch));
    }

    /// <summary>
    /// IR UDP 수신 서비스가 새 프레임을 디코딩했을 때 호출됩니다.
    /// 큰 화면/작은 화면 중 어디에 IR이 표시되는지에 따라 바인딩 이미지를 다시 갱신합니다.
    /// </summary>
    public void UpdateIrFrame(ImageSource? frame)
    {
        _irFrame = frame;
        OnPropertyChanged(nameof(LargeFeedImage));
        OnPropertyChanged(nameof(InsetFeedImage));
        OnPropertyChanged(nameof(LargeFeedStretch));
        OnPropertyChanged(nameof(InsetFeedStretch));
    }

    public void UpdateJetsonConnectionState(bool isConnected)
    {
        IsJetsonConnected = isConnected;
    }

    public void UpdateDetectionTargets(IReadOnlyList<DetectionTargetItem> targets)
    {
        DetectionTargets.Clear();
        foreach (var target in targets
                     .OrderByDescending(item => GetThreatWeight(item.ThreatLevel))
                     .ThenBy(item => item.ObjectId))
        {
            DetectionTargets.Add(target);
        }
    }

    private static ImageSource? RotateFrame(ImageSource? frame, double angle)
    {
        if (frame is not BitmapSource bitmap)
        {
            return frame;
        }

        if (Math.Abs(angle) < double.Epsilon)
        {
            return bitmap;
        }

        var transformed = new TransformedBitmap(bitmap, new RotateTransform(angle));
        transformed.Freeze();
        return transformed;
    }

    public void UpdateViewportSize(double width, double height)
    {
        _viewportWidth = Math.Max(width, 1);
        _viewportHeight = Math.Max(height, 1);
        ClampZoomPan();
        UpdateMiniMapViewport();
    }

    /// <summary>
    /// 사용자가 확대된 카메라 화면을 드래그할 때 호출됩니다.
    /// 확대 배율 안에서만 이동하도록 pan 값을 제한하고 미니맵 위치를 갱신합니다.
    /// </summary>
    public void PanZoom(double deltaX, double deltaY)
    {
        if (!ShowZoomMiniMap)
        {
            return;
        }

        _zoomPanX = Math.Clamp(_zoomPanX + deltaX, -GetMaxPanX(), GetMaxPanX());
        _zoomPanY = Math.Clamp(_zoomPanY + deltaY, -GetMaxPanY(), GetMaxPanY());

        OnPropertyChanged(nameof(ZoomTransformX));
        OnPropertyChanged(nameof(ZoomTransformY));
        UpdateMiniMapViewport();
    }

    /// <summary>
    /// 마우스 휠 입력으로 전자 줌 배율을 0.1 단위로 조정합니다.
    /// 시스템이 꺼져 있거나 휠 이동량이 없으면 아무 작업도 하지 않습니다.
    /// </summary>
    public void AdjustZoomByWheel(double wheelSteps)
    {
        if (!CanUseZoomControls || Math.Abs(wheelSteps) < double.Epsilon)
        {
            return;
        }

        // 휠 한 칸을 줌 배율 0.1 변화로 변환합니다.
        ZoomLevel += wheelSteps * 0.1;
    }

    private void SwapFeeds()
    {
        _isEoPrimary = !_isEoPrimary;
        OnPropertyChanged(nameof(IsEoPrimary));
        OnPropertyChanged(nameof(LargeFeedImage));
        OnPropertyChanged(nameof(InsetFeedImage));
        OnPropertyChanged(nameof(LargeFeedTitle));
        OnPropertyChanged(nameof(InsetFeedTitle));
        OnPropertyChanged(nameof(LargeFeedSubtitle));
        OnPropertyChanged(nameof(InsetFeedSubtitle));
        OnPropertyChanged(nameof(LargeFeedRotationAngle));
        OnPropertyChanged(nameof(InsetFeedRotationAngle));
        OnPropertyChanged(nameof(LargeFeedStretch));
        OnPropertyChanged(nameof(InsetFeedStretch));

    }

    public void RotateLargeFeedClockwise()
    {
        if (_isEoPrimary)
        {
            _eoDisplayRotationAngle = NextRotationAngle(_eoDisplayRotationAngle);
        }
        else
        {
            _irDisplayRotationAngle = NextRotationAngle(_irDisplayRotationAngle);
        }

        OnPropertyChanged(nameof(LargeFeedRotationAngle));
    }

    public void RotateInsetFeedClockwise()
    {
        if (_isEoPrimary)
        {
            _irDisplayRotationAngle = NextRotationAngle(_irDisplayRotationAngle);
        }
        else
        {
            _eoDisplayRotationAngle = NextRotationAngle(_eoDisplayRotationAngle);
        }

        OnPropertyChanged(nameof(InsetFeedRotationAngle));
    }

    private static double NextRotationAngle(double currentAngle) => (currentAngle + 90) % 360;

    private void ClampZoomPan()
    {
        _zoomPanX = Math.Clamp(_zoomPanX, -GetMaxPanX(), GetMaxPanX());
        _zoomPanY = Math.Clamp(_zoomPanY, -GetMaxPanY(), GetMaxPanY());
        OnPropertyChanged(nameof(ZoomTransformX));
        OnPropertyChanged(nameof(ZoomTransformY));
    }

    private double GetMaxPanX() => (_viewportWidth * (ZoomLevel - 1)) / 2;

    private double GetMaxPanY() => (_viewportHeight * (ZoomLevel - 1)) / 2;

    /// <summary>
    /// 줌 배율이나 pan 값이 바뀐 뒤 미니맵 사각형의 크기와 위치 바인딩을 갱신합니다.
    /// CameraView의 미니맵이 현재 큰 화면에서 보고 있는 영역을 따라가게 합니다.
    /// </summary>
    private void UpdateMiniMapViewport()
    {
        OnPropertyChanged(nameof(MiniMapViewportWidth));
        OnPropertyChanged(nameof(MiniMapViewportHeight));
        OnPropertyChanged(nameof(MiniMapViewportLeft));
        OnPropertyChanged(nameof(MiniMapViewportTop));
    }

    private static ImageSource CreateCameraPlaceholderFrame(string label, Color accentColor)
    {
        // 카메라 영상이 아직 없을 때 보여줄 어두운 배경과 안내 텍스트 이미지를 직접 그립니다.
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var background = new LinearGradientBrush(
                Color.FromRgb(23, 28, 36),
                Color.FromRgb(73, 25, 24),
                new Point(0, 0),
                new Point(1, 1));

            dc.DrawRectangle(background, null, new Rect(0, 0, 320, 240));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B)), null, new Point(220, 92), 46, 32);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 208, 90)), null, new Point(112, 152), 64, 22);

            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);
            for (var x = 0; x <= 320; x += 40)
            {
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, 240));
            }

            for (var y = 0; y <= 240; y += 40)
            {
                dc.DrawLine(gridPen, new Point(0, y), new Point(320, y));
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                var textBrush = new SolidColorBrush(Color.FromRgb(240, 243, 248));
                textBrush.Freeze();
                var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var formattedText = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    30,
                    textBrush,
                    1.0);
                dc.DrawText(formattedText, new Point(22, 20));
            }
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
}
