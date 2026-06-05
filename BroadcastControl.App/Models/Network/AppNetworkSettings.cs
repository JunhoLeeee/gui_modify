using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace BroadcastControl.App.Models.Network;

// GUI와 Jetson 사이에서 사용하는 IP, 포트, 녹화 경로 설정을 JSON 파일에서 읽고 저장합니다.
// IP와 포트 값의 실제 원본은 LigDnaGui.config.json입니다.
public sealed class AppNetworkSettings
{
    // 실행 파일 폴더에 저장되는 네트워크 설정 파일 이름입니다.
    private const string SettingsFileName = "LigDnaGui.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string JetsonHost { get; set; } = string.Empty;

    public string PcGuiHost { get; set; } = string.Empty;

    public string JetsonRecordingDir { get; set; } = string.Empty;

    public int EoUdpPort { get; set; }

    public int IrUdpPort { get; set; }

    public int DetectionUdpPort { get; set; }

    public int MotorControlPort { get; set; }

    public int TrackingRecordingControlPort { get; set; }

    public int MotorStatusPort { get; set; }

    public int RecordingHttpPort { get; set; }

    public int RecordingSegmentSeconds { get; set; }

    public int VlmUdpPort { get; set; }

    public string RecordedVideoUrl { get; set; } = string.Empty;

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    public static AppNetworkSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            throw new FileNotFoundException($"네트워크 설정 파일을 찾을 수 없습니다: {SettingsPath}", SettingsPath);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppNetworkSettings>(json, JsonOptions)
                ?? throw new InvalidOperationException($"네트워크 설정 파일을 읽을 수 없습니다: {SettingsPath}");

            settings.Normalize();
            settings.ValidateRequiredSettings();
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"네트워크 설정 JSON 형식이 올바르지 않습니다: {SettingsPath}", ex);
        }
    }

    public void Save()
    {
        Normalize();
        ValidateRequiredSettings();
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        // 네트워크 설정 드롭다운에 보여줄 실제 사용 가능한 GUI PC IPv4 주소 목록을 찾습니다.
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToList();
    }

    private void Normalize()
    {
        JetsonHost = Clean(JetsonHost);
        PcGuiHost = Clean(PcGuiHost);
        JetsonRecordingDir = Clean(JetsonRecordingDir);
        RecordedVideoUrl = Clean(RecordedVideoUrl);
        if (string.IsNullOrWhiteSpace(RecordedVideoUrl) &&
            !string.IsNullOrWhiteSpace(JetsonHost) &&
            IsValidPort(RecordingHttpPort))
        {
            RecordedVideoUrl = $"http://{JetsonHost}:{RecordingHttpPort}/";
        }
        else if (!string.IsNullOrWhiteSpace(RecordedVideoUrl) &&
                 !RecordedVideoUrl.EndsWith("/", StringComparison.Ordinal))
        {
            RecordedVideoUrl += "/";
        }
    }

    private void ValidateRequiredSettings()
    {
        RequireText(JetsonHost, nameof(JetsonHost));
        RequireText(PcGuiHost, nameof(PcGuiHost));
        RequireText(JetsonRecordingDir, nameof(JetsonRecordingDir));
        RequireText(RecordedVideoUrl, nameof(RecordedVideoUrl));
        RequirePort(EoUdpPort, nameof(EoUdpPort));
        RequirePort(IrUdpPort, nameof(IrUdpPort));
        RequirePort(DetectionUdpPort, nameof(DetectionUdpPort));
        RequirePort(MotorControlPort, nameof(MotorControlPort));
        RequirePort(TrackingRecordingControlPort, nameof(TrackingRecordingControlPort));
        RequirePort(MotorStatusPort, nameof(MotorStatusPort));
        RequirePort(RecordingHttpPort, nameof(RecordingHttpPort));

        if (RecordingSegmentSeconds is < 10 or > 3600)
        {
            throw new InvalidOperationException($"{SettingsFileName}의 {nameof(RecordingSegmentSeconds)} 값은 10~3600 사이여야 합니다.");
        }
    }

    private static void RequireText(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{SettingsFileName}에 {propertyName} 값이 필요합니다.");
        }
    }

    private static void RequirePort(int port, string propertyName)
    {
        if (!IsValidPort(port))
        {
            throw new InvalidOperationException($"{SettingsFileName}의 {propertyName} 값은 1~65535 사이여야 합니다.");
        }
    }

    private static bool IsValidPort(int port)
    {
        return port is > 0 and <= 65535;
    }

    private static string Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
