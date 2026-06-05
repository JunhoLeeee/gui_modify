using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using BroadcastControl.App.Models.Camera;

namespace BroadcastControl.App.Services;

// VLM_GUI_PORT(기본 7000)에서 VLM 추론 결과 바이너리 패킷을 수신합니다.
// 패킷 구조: [count: uint16][track_id: int32][behavior: int32][weapon: int32][job: int32] × count
// count=N 이면 총 패킷 크기는 2 + N×16 바이트입니다.
public sealed class VlmUdpReceiverService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public VlmUdpReceiverService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public event Action<IReadOnlyList<VlmResult>>? ResultsReceived;

    public bool Start(int port)
    {
        if (_udpClient is not null)
        {
            return true;
        }

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.ExclusiveAddressUse = false;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _cts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _udpClient?.Close(); } catch { }
        _udpClient?.Dispose();
        _udpClient = null;
        try { _receiveTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _receiveTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpClient is null) break;
                var result = await _udpClient.ReceiveAsync(ct);
                var parsed = TryParsePacket(result.Buffer);
                if (parsed is not null && parsed.Count > 0)
                {
                    _dispatcher.BeginInvoke(() => ResultsReceived?.Invoke(parsed));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private static IReadOnlyList<VlmResult>? TryParsePacket(byte[] data)
    {
        if (data.Length < 2)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        if (count == 0 || data.Length < 2 + count * 16)
        {
            return null;
        }

        var results = new List<VlmResult>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = 2 + i * 16;
            var trackId  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset,      4));
            var behavior = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4,  4));
            var weapon   = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8,  4));
            var job      = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 12, 4));
            results.Add(new VlmResult(
                trackId,
                Enum.IsDefined(typeof(VlmBehavior), behavior) ? (VlmBehavior)behavior : VlmBehavior.Unknown,
                Enum.IsDefined(typeof(VlmWeapon),   weapon)   ? (VlmWeapon)weapon     : VlmWeapon.Unknown,
                Enum.IsDefined(typeof(VlmJob),      job)      ? (VlmJob)job           : VlmJob.Unknown));
        }

        return results;
    }
}
