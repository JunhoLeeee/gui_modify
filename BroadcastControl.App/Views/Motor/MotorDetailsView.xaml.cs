using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;

// 파일 역할:
// 모터 상세 상태 오버레이에서 배경 클릭을 감지합니다.
// 사용자가 상세창 바깥을 누르면 MainWindow가 오버레이를 닫을 수 있도록 이벤트를 전달합니다.

namespace BroadcastControl.App.Views.Motor
{
public partial class MotorDetailsView : UserControl
{
    public MotorDetailsView()
    {
        InitializeComponent();
    }

    public event MouseButtonEventHandler? BackdropMouseLeftButtonDownRequested;

    private void MotorDetailsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BackdropMouseLeftButtonDownRequested?.Invoke(sender, e);
    }
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
    private void MotorDetailsBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsMotorDetailsOpen)
        {
            _viewModel.IsMotorDetailsOpen = false;
            e.Handled = true;
        }
    }
}
}
