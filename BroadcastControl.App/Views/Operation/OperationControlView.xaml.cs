using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using BroadcastControl.App.Models.Network;

// 파일 역할:
// 하단 조작 패널에서 Scan/Manual 모드 버튼과 네트워크 설정 저장 버튼을 연결합니다.
// GUI IP와 Jetson IP 입력 컨트롤을 MainWindow가 읽고 저장할 수 있도록 접근자를 제공합니다.

namespace BroadcastControl.App.Views.Operation
{
public partial class OperationControlView : UserControl
{
    public OperationControlView()
    {
        InitializeComponent();
    }

    public TextBox JetsonHostTextBoxElement => JetsonHostTextBox;

    public ComboBox PcGuiHostComboBoxElement => PcGuiHostComboBox;

    public event RoutedEventHandler? ManualModeClicked;
    public event RoutedEventHandler? SaveNetworkSettingsClicked;

    private void Button_Click(object sender, RoutedEventArgs e) => ManualModeClicked?.Invoke(sender, e);

    private void SaveNetworkSettingsButton_OnClick(object sender, RoutedEventArgs e) => SaveNetworkSettingsClicked?.Invoke(sender, e);
}
}

namespace BroadcastControl.App
{
public partial class MainWindow : Window
{
    private void Button_Click(object sender, RoutedEventArgs e)
    {
    }

    private void LoadNetworkSettingsEditor()
    {
        OperationActiveView.JetsonHostTextBoxElement.Text = _networkSettings.JetsonHost;

        var localAddresses = AppNetworkSettings.GetLocalIpv4Addresses();
        OperationActiveView.PcGuiHostComboBoxElement.ItemsSource = localAddresses;
        OperationActiveView.PcGuiHostComboBoxElement.Text = _networkSettings.PcGuiHost;
        if (localAddresses.Count > 0 && !localAddresses.Contains(_networkSettings.PcGuiHost, StringComparer.Ordinal))
        {
            _viewModel.AppendImportantLog($"현재 GUI IP 후보: {string.Join(", ", localAddresses)}");
        }
    }

    private void SaveNetworkSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _networkSettings.JetsonHost = OperationActiveView.JetsonHostTextBoxElement.Text;
        _networkSettings.PcGuiHost = OperationActiveView.PcGuiHostComboBoxElement.Text;
        _networkSettings.RecordedVideoUrl = $"http://{_networkSettings.JetsonHost.Trim()}:{_networkSettings.RecordingHttpPort.ToString(CultureInfo.InvariantCulture)}/";
        _networkSettings.Save();
        _viewModel.Motor.ConfigureNetwork(_networkSettings);

        _viewModel.AppendImportantLog($"네트워크 설정을 저장하고 즉시 적용했습니다: Jetson {_networkSettings.JetsonHost}, GUI {_networkSettings.PcGuiHost}");
        MessageBox.Show(
            "네트워크 설정을 저장했습니다.\n\n모터 명령과 녹화 영상 주소는 즉시 새 Jetson IP를 사용합니다.\nGUI IP는 다음에 Jetson 브리지가 시작될 때 송출 대상 설정에도 반영됩니다.",
            "Network",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
}
