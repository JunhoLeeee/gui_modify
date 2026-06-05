using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace BroadcastControl.App;

/// <summary>
/// 프로그램 시작 시 메인 윈도우를 만들고, 시스템 테마 또는 설정값에 맞는 전역 색상 리소스를 적용합니다.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 현재 앱 전체에 적용된 밝은/어두운 테마 상태입니다.
    /// </summary>
    public AppThemeMode CurrentThemeMode { get; private set; } = AppThemeMode.Dark;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Windows 개인 설정의 앱 테마를 읽어 초기 화면 테마를 맞춥니다.
        ApplyTheme(GetSystemThemeMode());
        base.OnStartup(e);

        // WPF 진입점에서 MainWindow를 직접 생성해 앱의 DataContext와 각 View 연결을 시작합니다.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// MaterialDesign 테마와 앱 ResourceDictionary의 색상 브러시를 함께 갱신합니다.
    /// 설정창의 Dark/Light 버튼에서 호출되어 전체 화면 색상을 즉시 바꿉니다.
    /// </summary>
    public void ApplyTheme(AppThemeMode themeMode)
    {
        CurrentThemeMode = themeMode;

        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();
        theme.SetBaseTheme(themeMode == AppThemeMode.Dark ? BaseTheme.Dark : BaseTheme.Light);
        paletteHelper.SetTheme(theme);

        if (themeMode == AppThemeMode.Dark)
        {
            // 어두운 테마에서 카메라, 설정창, 상단 버튼, 배경에 사용할 전역 브러시입니다.
            SetBrushColor("WindowBackgroundBrush", "#FF161B24");
            SetBrushColor("PanelBrush", "#FF242A35");
            SetBrushColor("PanelBorderBrush", "#FF464E5D");
            SetBrushColor("AccentBrush", "#FFE09A36");
            SetBrushColor("PrimaryTextBrush", "#FFF0F3F8");
            SetBrushColor("SecondaryTextBrush", "#FFC7CDD8");
            SetBrushColor("SurfaceAltBrush", "#FF0C1018");
            SetBrushColor("OverlayPanelBrush", "#DD111722");
            SetBrushColor("DrawerBrush", "#FF202631");
            SetBrushColor("DrawerBorderBrush", "#FF465061");
            SetBrushColor("DrawerTextBrush", "#FFF0F3F8");
            SetBrushColor("DrawerItemBrush", "#FF2C3442");
            SetBrushColor("DrawerItemBorderBrush", "#FF4A5567");
            SetBrushColor("TopBarButtonBrush", "#FF2E3643");
            SetBrushColor("TopBarButtonHoverBrush", "#FF394353");
            SetBrushColor("TopBarButtonPressedBrush", "#FF465164");
            SetBackdropBrush("#FF1C2230", "#FF1A2742", "#FF141A25", "#FF161B24");
            return;
        }

        // 밝은 테마에서 카메라, 설정창, 상단 버튼, 배경에 사용할 전역 브러시입니다.
        SetBrushColor("WindowBackgroundBrush", "#FFE7E8EB");
        SetBrushColor("PanelBrush", "#FFF8F8F9");
        SetBrushColor("PanelBorderBrush", "#FFCBCDD2");
        SetBrushColor("AccentBrush", "#FF4E68D1");
        SetBrushColor("PrimaryTextBrush", "#FF1E2329");
        SetBrushColor("SecondaryTextBrush", "#FF616873");
        SetBrushColor("SurfaceAltBrush", "#FFEBE4D8");
        SetBrushColor("OverlayPanelBrush", "#F3FFFFFF");
        SetBrushColor("DrawerBrush", "#FFF2F3F5");
        SetBrushColor("DrawerBorderBrush", "#FFC8CBD0");
        SetBrushColor("DrawerTextBrush", "#FF1E2329");
        SetBrushColor("DrawerItemBrush", "#FFE7E9EC");
        SetBrushColor("DrawerItemBorderBrush", "#FFBEC2C8");
        SetBrushColor("TopBarButtonBrush", "#FFE3E6EA");
        SetBrushColor("TopBarButtonHoverBrush", "#FFD8DCE2");
        SetBrushColor("TopBarButtonPressedBrush", "#FFCDD2D9");
        SetBackdropBrush("#FFF7F8FB", "#FFEAEFF8", "#FFE4E9F2", "#FFE7E8EB");
    }

    /// <summary>
    /// Windows 레지스트리의 AppsUseLightTheme 값을 읽어 앱 시작 테마를 결정합니다.
    /// 읽기에 실패하면 현장 화면에서 눈부심이 적은 어두운 테마를 기본값으로 사용합니다.
    /// </summary>
    private AppThemeMode GetSystemThemeMode()
    {
        try
        {
            const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var key = Registry.CurrentUser.OpenSubKey(personalizeKey);
            var appsUseLightTheme = key?.GetValue("AppsUseLightTheme");

            return appsUseLightTheme is int lightThemeFlag && lightThemeFlag > 0
                ? AppThemeMode.Light
                : AppThemeMode.Dark;
        }
        catch
        {
            // 레지스트리 접근이 막힌 환경에서도 앱은 어두운 테마로 계속 실행합니다.
            return AppThemeMode.Dark;
        }
    }

    /// <summary>
    /// XAML에서 사용하는 브러시 리소스 하나를 지정한 ARGB 색상으로 교체합니다.
    /// </summary>
    private void SetBrushColor(string resourceKey, string colorCode)
    {
        if (ColorConverter.ConvertFromString(colorCode) is not Color color)
        {
            return;
        }

        Resources[resourceKey] = new SolidColorBrush(color);
    }

    /// <summary>
    /// 메인 윈도우 배경에 쓰는 세로 그라데이션 브러시를 테마별 색상으로 다시 만듭니다.
    /// </summary>
    private void SetBackdropBrush(string startColor, string accentColor, string midColor, string endColor)
    {
        if (ColorConverter.ConvertFromString(startColor) is not Color start ||
            ColorConverter.ConvertFromString(accentColor) is not Color accent ||
            ColorConverter.ConvertFromString(midColor) is not Color mid ||
            ColorConverter.ConvertFromString(endColor) is not Color end)
        {
            return;
        }

        Resources["WindowBackdropBrush"] = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(start, 0.0),
                new(accent, 0.08),
                new(mid, 0.16),
                new(end, 1.0),
            },
            new Point(0, 0),
            new Point(0, 1));
    }
}

/// <summary>
/// 앱 전체 테마 선택값입니다.
/// </summary>
public enum AppThemeMode
{
    Light,
    Dark,
}
