// 애플리케이션 전체 시작점과 테마 리소스를 관리하는 파일이다.
// 프로그램이 실행될 때 시스템 테마를 읽어 Material Design 테마와 앱 전용 색상 브러시를 맞추고,
// MainWindow를 직접 생성해 GUI 화면을 띄운다.
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace BroadcastControl.App;

/// <summary>
/// 앱 전체 테마와 공통 색상 리소스를 관리하는 진입점임.
/// 시스템 테마 감지, Material Design 테마 갱신, 자체 브러시 갱신을 담당함.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 현재 앱 테마 상태임. 설정창 버튼 상태와 테마 전환 기준으로 사용함.
    /// </summary>
    public AppThemeMode CurrentThemeMode { get; private set; } = AppThemeMode.Dark;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 창 생성 전에 시스템 테마를 먼저 읽어 초기 색상 리소스를 맞춤.
        ApplyTheme(GetSystemThemeMode());
        base.OnStartup(e);

        // StartupUri 대신 코드에서 창을 생성해 테마 적용 순서를 명확히 함.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// 앱 전체 테마를 다크 또는 라이트로 적용함.
    /// Material Design 테마와 직접 정의한 브러시를 함께 갱신함.
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
            // theme5 다크 테마: 전술 관제 SW 느낌의 네이비-그래파이트 팔레트임.
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

        // theme5 라이트 테마: 밝은 작업 캔버스와 회백색 패널 중심 팔레트임.
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
    /// Windows 앱 테마 설정을 읽어 기본 테마를 결정함.
    /// 레지스트리 접근 실패 시 안전하게 다크 테마로 시작함.
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
            // 시스템 테마 조회 실패가 앱 실행 실패로 이어지지 않도록 기본값 사용.
            return AppThemeMode.Dark;
        }
    }

    /// <summary>
    /// 브러시 리소스를 새 SolidColorBrush로 교체함.
    /// Freeze된 공유 브러시를 직접 수정하지 않기 위한 처리임.
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
    /// 화면 최하단 배경 그라데이션을 교체함.
    /// 라이트/다크 테마 전환 시 배경 이질감을 줄이기 위한 처리임.
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
/// 앱에서 지원하는 테마 종류임.
/// </summary>
public enum AppThemeMode
{
    Light,
    Dark,
}
