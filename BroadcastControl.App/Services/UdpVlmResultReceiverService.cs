// 외부 VLM 분석 결과를 UDP로 받는 서비스 파일이다.
// 6002/udp에서 JSON 또는 일반 텍스트 메시지를 받아 전체 위험도, 분석 문장, 객체별 위험도 맵으로 정리한다.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BroadcastControl.App.Services;

public sealed class UdpVlmResultReceiverService : IDisposable
{
    private const int DefaultPort = 6003;

    private readonly UdpClient _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;

    public UdpVlmResultReceiverService(int? port = null)
    {
        Port = ResolvePort(port);
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
    }

    public event EventHandler<VlmResultPacket>? ResultReceived;

    public event EventHandler<string>? ReceiverError;

    public int Port { get; }

    public void Start()
    {
        if (_receiveTask is { IsCompleted: false })
        {
            return;
        }

        // VLM 결과 수신도 UI를 막지 않도록 백그라운드에서 계속 대기한다.
        _cancellationTokenSource = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cancellationTokenSource.Token));
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _udpClient.Dispose();
        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The receive loop exits through cancellation or socket disposal during shutdown.
        }

        _cancellationTokenSource?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                var packet = ParsePacket(result.Buffer);
                if (!string.IsNullOrWhiteSpace(packet.AnalysisMessage))
                {
                    ResultReceived?.Invoke(this, packet);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                ReceiverError?.Invoke(this, ex.Message);
            }
        }
    }

    private static VlmResultPacket ParsePacket(byte[] buffer)
    {
        // 현재는 JSON과 일반 텍스트를 모두 허용한다.
        // 실험 중 VLM 송신 포맷이 바뀌어도 최소한 분석 문장은 화면에 표시되도록 하기 위해서다.
        var text = DecodeText(buffer);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new VlmResultPacket(string.Empty, string.Empty, string.Empty, null, EmptyThreatMap(), DateTime.Now);
        }

        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                // 필드 이름은 팀원 구현에 따라 조금씩 달라질 수 있어 여러 후보 이름을 허용한다.
                var threatLevel = ReadString(root, "threatLevel", "riskLevel", "risk", "threat", "level") ?? string.Empty;
                var analysisMessage =
                    ReadString(root, "analysisMessage", "vlmAnalysis", "analysis", "message", "result") ?? text;
                var detectionSummary =
                    ReadString(root, "detectionSummary", "detections", "tracks", "objects") ?? string.Empty;
                var frameId = ReadUInt(root, "frameId", "frame_id");
                var objectThreatLevels = ReadObjectThreatLevels(root);

                return new VlmResultPacket(threatLevel, analysisMessage, detectionSummary, frameId, objectThreatLevels, DateTime.Now);
            }
            catch (JsonException)
            {
                return new VlmResultPacket(string.Empty, text, string.Empty, null, EmptyThreatMap(), DateTime.Now);
            }
        }

        return new VlmResultPacket(string.Empty, text, string.Empty, null, EmptyThreatMap(), DateTime.Now);
    }

    private static string DecodeText(byte[] buffer)
    {
        var offset = 0;
        if (buffer.Length >= 4 && Encoding.ASCII.GetString(buffer, 0, 4) == "VLMR")
        {
            // VLMR prefix는 바이너리 패킷임을 표시하기 위한 4B 식별자이므로 실제 메시지에서는 제외한다.
            offset = 4;
        }

        return Encoding.UTF8.GetString(buffer, offset, buffer.Length - offset).Trim('\0', ' ', '\r', '\n', '\t');
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                    JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static uint? ReadUInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                uint.TryParse(value.GetString(), out var parsedNumber))
            {
                return parsedNumber;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<int, string> ReadObjectThreatLevels(JsonElement root)
    {
        // VLM 쪽 포맷이 조금 바뀌어도 받을 수 있도록 대표적인 키 이름들을 모두 허용한다.
        foreach (var name in new[] { "objectThreats", "object_threats", "trackThreats", "track_threats", "detections", "tracks", "objects" })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var threats = new Dictionary<int, string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var objectId = ReadInt(item, "objectId", "object_id", "trackId", "track_id", "id");
                var threatLevel = ReadString(item, "threatLevel", "riskLevel", "risk", "threat", "level");
                if (objectId is null || string.IsNullOrWhiteSpace(threatLevel))
                {
                    continue;
                }

                threats[objectId.Value] = threatLevel;
            }

            if (threats.Count > 0)
            {
                return threats;
            }
        }

        return EmptyThreatMap();
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsedNumber))
            {
                return parsedNumber;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<int, string> EmptyThreatMap() => new Dictionary<int, string>();

    private static int ResolvePort(int? port)
    {
        if (port is > 0 and <= 65535)
        {
            return port.Value;
        }

        var envPort = Environment.GetEnvironmentVariable("VLM_RESULT_PORT");
        return int.TryParse(envPort, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535
            ? parsedPort
            : DefaultPort;
    }
}
