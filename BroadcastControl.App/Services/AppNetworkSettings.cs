// GUI가 사용하는 네트워크 주소와 포트 설정을 JSON 파일로 읽고 쓰는 설정 파일이다.
// Network 영역에서 GUI IP 또는 Jetson IP를 저장하면 LigDnaGui.config.json에 반영되고,
// 다음 GUI 실행 또는 Jetson bridge 실행 스크립트에서 같은 값을 재사용할 수 있다.
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace BroadcastControl.App.Services;

public sealed class AppNetworkSettings
{
    // 설정 파일은 실행 파일과 같은 폴더에 복사된다.
    // 배포된 exe를 다른 PC에서 실행해도 같은 위치의 JSON만 수정하면 네트워크 환경을 바꿀 수 있다.
    private const string SettingsFileName = "LigDnaGui.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // Jetson IP: 영상 HTTP 서버와 모터 UDP 명령을 보낼 대상 주소다.
    public string JetsonHost { get; set; } = "192.168.3.143";

    // GUI IP: Jetson bridge가 EO/IR UDP 영상을 송출할 PC 주소다.
    public string PcGuiHost { get; set; } = "192.168.1.94";

    // Jetson 내부에서 녹화 파일이 저장되는 기본 폴더다.
    public string JetsonRecordingDir { get; set; } = "/home/lig/Desktop/video";

    // 아래 포트들은 Jetson bridge와 GUI가 맞춰 쓰는 고정 통신 규격이다.
    public int EoUdpPort { get; set; } = 6000;

    public int IrUdpPort { get; set; } = 6001;

    public int DetectionUdpPort { get; set; } = 6002;

    public int VlmResultPort { get; set; } = 6003;

    public int MotorControlPort { get; set; } = 8000;

    public int TrackingRecordingControlPort { get; set; } = 8010;

    public int MotorStatusPort { get; set; } = 8001;

    public int MobileAlertPort { get; set; } = 8088;

    public int RecordingHttpPort { get; set; } = 8090;

    public int RecordingSegmentSeconds { get; set; } = 60;

    public string RecordedVideoUrl { get; set; } = "http://192.168.3.143:8090/";

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    public static AppNetworkSettings Load()
    {
        // 실행 폴더의 JSON을 먼저 읽고, 없거나 깨져 있으면 기본값으로 시작한다.
        // 이후 환경변수가 있으면 현장 테스트용 override로 반영한다.
        AppNetworkSettings settings;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppNetworkSettings>(json, JsonOptions) ?? new AppNetworkSettings();
            }
            else
            {
                settings = new AppNetworkSettings();
            }
        }
        catch
        {
            settings = new AppNetworkSettings();
        }

        settings.ApplyEnvironmentOverrides();
        settings.Normalize();
        settings.SaveIfMissing();
        return settings;
    }

    public void Save()
    {
        // 저장 전 Normalize를 거쳐 빈 값/잘못된 포트가 JSON에 남지 않도록 한다.
        Normalize();
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        // PC에 연결된 네트워크 어댑터 중 실제 사용 가능한 IPv4 주소만 드롭다운 후보로 보여준다.
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

    private void ApplyEnvironmentOverrides()
    {
        JetsonHost = GetEnvironment("JETSON_HOST", JetsonHost);
        PcGuiHost = GetEnvironment("JETSON_GUI_HOST", PcGuiHost);
        JetsonRecordingDir = GetEnvironment("JETSON_RECORDING_DIR", JetsonRecordingDir);
        RecordedVideoUrl = GetEnvironment("JETSON_VIDEO_URL", RecordedVideoUrl);
        EoUdpPort = GetIntEnvironment("EO_GUI_PORT", EoUdpPort);
        IrUdpPort = GetIntEnvironment("IR_GUI_PORT", IrUdpPort);
        DetectionUdpPort = GetIntEnvironment("DETECTION_GUI_PORT", DetectionUdpPort);
        VlmResultPort = GetIntEnvironment("VLM_RESULT_PORT", VlmResultPort);
        MotorControlPort = GetIntEnvironment("MOTOR_CONTROL_PORT", MotorControlPort);
        TrackingRecordingControlPort = GetIntEnvironment("TRACKING_RECORDING_CONTROL_PORT", TrackingRecordingControlPort);
        MotorStatusPort = GetIntEnvironment("MOTOR_STATUS_PORT", MotorStatusPort);
        MobileAlertPort = GetIntEnvironment("MOBILE_ALERT_PORT", MobileAlertPort);
        RecordingHttpPort = GetIntEnvironment("RECORDING_HTTP_PORT", RecordingHttpPort);
        RecordingSegmentSeconds = GetIntEnvironment("RECORDING_SEGMENT_SECONDS", RecordingSegmentSeconds);
    }

    private void Normalize()
    {
        // 잘못 입력된 값은 기본값으로 되돌리고 포트는 1~65535 범위로 제한한다.
        // RecordedVideoUrl은 JetsonHost와 RecordingHttpPort를 기준으로 자동 보정한다.
        JetsonHost = Clean(JetsonHost, "192.168.3.143");
        PcGuiHost = Clean(PcGuiHost, "192.168.1.94");
        JetsonRecordingDir = Clean(JetsonRecordingDir, "/home/lig/Desktop/video");
        EoUdpPort = ClampPort(EoUdpPort, 6000);
        IrUdpPort = ClampPort(IrUdpPort, 6001);
        DetectionUdpPort = ClampPort(DetectionUdpPort, 6002);
        VlmResultPort = ClampPort(VlmResultPort, 6003);
        if (VlmResultPort == DetectionUdpPort)
        {
            VlmResultPort = 6003;
        }
        MotorControlPort = ClampPort(MotorControlPort, 8000);
        TrackingRecordingControlPort = ClampPort(TrackingRecordingControlPort, 8010);
        MotorStatusPort = ClampPort(MotorStatusPort, 8001);
        MobileAlertPort = ClampPort(MobileAlertPort, 8088);
        RecordingHttpPort = ClampPort(RecordingHttpPort, 8090);
        RecordingSegmentSeconds = Math.Clamp(RecordingSegmentSeconds, 10, 3600);
        RecordedVideoUrl = Clean(RecordedVideoUrl, $"http://{JetsonHost}:{RecordingHttpPort}/");
        if (!RecordedVideoUrl.EndsWith("/", StringComparison.Ordinal))
        {
            RecordedVideoUrl += "/";
        }
    }

    private void SaveIfMissing()
    {
        if (!File.Exists(SettingsPath))
        {
            Save();
        }
    }

    private static string Clean(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ClampPort(int port, int fallback)
    {
        return port is > 0 and <= 65535 ? port : fallback;
    }

    private static string GetEnvironment(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int GetIntEnvironment(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535 ? parsed : fallback;
    }

}
