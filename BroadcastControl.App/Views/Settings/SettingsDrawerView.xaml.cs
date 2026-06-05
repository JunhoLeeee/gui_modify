using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.ComponentModel;
using System.Globalization;
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
// 설정 drawer에서 주 탐지체, 테마, 언어, 화면 모드, 네트워크 입력 UI를 연결합니다.
// 배경 클릭으로 drawer를 닫고, 창 모드 전환 버튼 이벤트를 MainWindow로 전달합니다.

namespace BroadcastControl.App.Views.Settings
{
public partial class SettingsDrawerView : UserControl
{
    public SettingsDrawerView()
    {
        InitializeComponent();
    }

    public Rectangle SettingsBackdropElement => SettingsBackdrop;

    public Border SettingsDrawerElement => SettingsDrawer;

    public TranslateTransform SettingsDrawerTransformElement => SettingsDrawerTransform;

    public Button WindowModeToggleButtonElement => WindowModeToggleButton;

    public event MouseButtonEventHandler? BackdropMouseLeftButtonDownRequested;
    public event RoutedEventHandler? WindowModeToggleClicked;

    private void SettingsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BackdropMouseLeftButtonDownRequested?.Invoke(sender, e);

    private void WindowModeToggleButton_OnClick(object sender, RoutedEventArgs e) => WindowModeToggleClicked?.Invoke(sender, e);
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
    private void SettingsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.IsSettingsOpen = false;
        }
    }

    private void Button_Click_1(object sender, RoutedEventArgs e)
    {
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
        if (SettingsActiveView.WindowModeToggleButtonElement is null)
        {
            return;
        }

        SettingsActiveView.WindowModeToggleButtonElement.Content = _isFullscreenMode
            ? _viewModel.Text["WindowMode"]
            : _viewModel.Text["FullscreenMode"];
    }
    private void AnimateSettingsDrawer(bool isOpen, bool animate)
    {
        if (!animate)
        {
            SettingsActiveView.SettingsBackdropElement.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsActiveView.SettingsBackdropElement.IsHitTestVisible = isOpen;
            SettingsActiveView.SettingsBackdropElement.Opacity = isOpen ? 1.0 : 0.0;

            SettingsActiveView.SettingsDrawerElement.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsActiveView.SettingsDrawerElement.Opacity = isOpen ? 1.0 : 0.0;
            SettingsActiveView.SettingsDrawerTransformElement.X = isOpen ? 0 : SettingsDrawerClosedOffset;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(isOpen ? 220 : 170);
        var easing = new CubicEase
        {
            EasingMode = isOpen ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        if (isOpen)
        {
            SettingsActiveView.SettingsBackdropElement.Visibility = Visibility.Visible;
            SettingsActiveView.SettingsBackdropElement.IsHitTestVisible = true;
            SettingsActiveView.SettingsDrawerElement.Visibility = Visibility.Visible;
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
                SettingsActiveView.SettingsBackdropElement.Visibility = Visibility.Collapsed;
                SettingsActiveView.SettingsBackdropElement.IsHitTestVisible = false;
                SettingsActiveView.SettingsDrawerElement.Visibility = Visibility.Collapsed;
            };
        }

        SettingsActiveView.SettingsBackdropElement.BeginAnimation(OpacityProperty, backdropAnimation, HandoffBehavior.SnapshotAndReplace);
        SettingsActiveView.SettingsDrawerElement.BeginAnimation(OpacityProperty, drawerOpacityAnimation, HandoffBehavior.SnapshotAndReplace);
        SettingsActiveView.SettingsDrawerTransformElement.BeginAnimation(TranslateTransform.XProperty, drawerSlideAnimation, HandoffBehavior.SnapshotAndReplace);
    }
}
}
