using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BroadcastControl.App.Models.Camera;
using BroadcastControl.App.Models.Motor;
using BroadcastControl.App.Models.Network;
using BroadcastControl.App.ViewModels;

// 파일 역할:
// 모터 조작 패널의 버튼과 입력창 이벤트를 외부로 전달합니다.
// 방향키 누름/뗌, 각도 입력, Motor Speed 조절, 모터 상세창 열기 이벤트를 연결합니다.

namespace BroadcastControl.App.Views.Motor
{
public partial class MotorControlView : UserControl
{
    public MotorControlView()
    {
        InitializeComponent();
    }

    public Button MotorPadUpButtonElement => MotorPadUpButton;

    public Button MotorPadLeftButtonElement => MotorPadLeftButton;

    public Button MotorPadCenterButtonElement => MotorPadCenterButton;

    public Button MotorPadRightButtonElement => MotorPadRightButton;

    public Button MotorPadDownButtonElement => MotorPadDownButton;

    public event RoutedEventHandler? RotateAuxCameraRequested;
    public event RoutedEventHandler? MotorTargetEnterClicked;
    public event MouseButtonEventHandler? MotorButtonPreviewMouseLeftButtonDownRequested;
    public event MouseButtonEventHandler? MotorButtonPreviewMouseLeftButtonUpRequested;
    public event MouseEventHandler? MotorButtonMouseLeaveRequested;

    private void RotateAuxCameraButton_OnClick(object sender, RoutedEventArgs e) => RotateAuxCameraRequested?.Invoke(sender, e);

    private void Button_Click_2(object sender, RoutedEventArgs e) => MotorTargetEnterClicked?.Invoke(sender, e);

    private void MotorButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => MotorButtonPreviewMouseLeftButtonDownRequested?.Invoke(sender, e);

    private void MotorButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => MotorButtonPreviewMouseLeftButtonUpRequested?.Invoke(sender, e);

    private void MotorButton_OnMouseLeave(object sender, MouseEventArgs e) => MotorButtonMouseLeaveRequested?.Invoke(sender, e);
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
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

        if (direction != "Center")
        {
            SendActiveMotorButtons();
        }

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
        SetMotorPadButtonActive(MotorActiveView.MotorPadLeftButtonElement, _activeMotorDirections.ContainsKey("Left"));
        SetMotorPadButtonActive(MotorActiveView.MotorPadRightButtonElement, _activeMotorDirections.ContainsKey("Right"));
        SetMotorPadButtonActive(MotorActiveView.MotorPadUpButtonElement, _activeMotorDirections.ContainsKey("Up"));
        SetMotorPadButtonActive(MotorActiveView.MotorPadDownButtonElement, _activeMotorDirections.ContainsKey("Down"));
        SetMotorPadButtonActive(MotorActiveView.MotorPadCenterButtonElement, _activeMotorDirections.ContainsKey("Center"));
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

    private void Button_Click_2(object sender, RoutedEventArgs e)
    {

    }
}
}
