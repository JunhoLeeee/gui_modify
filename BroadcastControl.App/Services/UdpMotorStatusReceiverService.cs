// Jetson/Thor에서 오는 모터 상태 UDP 패킷을 수신하고 파싱하는 서비스 파일이다.
// 8001/udp로 들어오는 pan/tilt 상태를 MotorStatusSnapshot으로 변환해 ViewModel이 각도와 진단 값을 표시할 수 있게 한다.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BroadcastControl.App.Services;

public sealed class UdpMotorStatusReceiverService : IDisposable
{
    private const int DefaultPort = 8001;
    private const int CurrentPacketSize = 18;
    private const int CurrentSnapshotSize = CurrentPacketSize * 2;
    private const int LegacyPacketSize = 32;
    private const int LegacySnapshotSize = LegacyPacketSize * 2;

    private readonly UdpClient _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;

    public UdpMotorStatusReceiverService(int? port = null)
    {
        Port = ResolvePort(port);
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
    }

    public event EventHandler<MotorStatusSnapshot>? StatusReceived;

    public event EventHandler<string>? ReceiverError;

    public int Port { get; }

    public void Start()
    {
        if (_receiveTask is { IsCompleted: false })
        {
            return;
        }

        // 모터 상태는 영상과 별도 스레드에서 받는다.
        // 이렇게 해야 영상 수신량이 많아져도 pan/tilt 상태 표시가 늦게 갱신되는 일을 줄일 수 있다.
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
        // 수신 루프는 별도 Task에서 계속 대기한다.
        // 패킷이 정상 파싱되면 StatusReceived 이벤트로 ViewModel 갱신 흐름에 넘긴다.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                if (TryParseSnapshot(result.Buffer, out var snapshot))
                {
                    StatusReceived?.Invoke(this, snapshot);
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

    private static bool TryParseSnapshot(byte[] buffer, out MotorStatusSnapshot snapshot)
    {
        snapshot = default;
        if (buffer.Length < CurrentSnapshotSize && buffer.Length < LegacyPacketSize)
        {
            return false;
        }

        // 최신 36B 패킷과 이전 32B 단일 모터 패킷을 모두 구분해서 파싱한다.
        // 실제 화면에는 두 모터 값이 들어온 경우 pan/tilt를 나눠서 표시한다.
        var receivedAt = DateTime.Now;
        var isLegacyPacket = buffer.Length >= LegacySnapshotSize || buffer.Length == LegacyPacketSize;
        var packetSize = isLegacyPacket ? LegacyPacketSize : CurrentPacketSize;
        var pan = isLegacyPacket
            ? ParseLegacyPacket(buffer.AsSpan(0, packetSize), receivedAt)
            : ParseCurrentPacket(buffer.AsSpan(0, packetSize), receivedAt);
        MotorStatusPacket? tilt = null;
        if (buffer.Length >= packetSize * 2)
        {
            tilt = isLegacyPacket
                ? ParseLegacyPacket(buffer.AsSpan(packetSize, packetSize), receivedAt)
                : ParseCurrentPacket(buffer.AsSpan(packetSize, packetSize), receivedAt);
        }

        snapshot = new MotorStatusSnapshot(pan, tilt);
        return true;
    }

    private static MotorStatusPacket ParseCurrentPacket(ReadOnlySpan<byte> buffer, DateTime receivedAt)
    {
        // 현재 패킷은 Dynamixel 원본 필드 순서를 거의 그대로 따른다.
        // position/velocity는 4B, 전압/전류/PWM은 little-endian 정수로 읽는다.
        var presentPosition = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(10, 4));
        var presentVelocity = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(6, 4));
        return new MotorStatusPacket(
            HardwareErrorStatus: buffer[17],
            PresentTemperature: buffer[16],
            PresentInputVoltageRaw: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14, 2)),
            PresentPosition: presentPosition,
            PresentVelocity: presentVelocity,
            PresentCurrentRaw: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2)),
            PresentPwm: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2)),
            GoalPosition: presentPosition,
            GoalVelocity: 0,
            Moving: buffer[0],
            MovingStatus: buffer[1],
            ReceivedAt: receivedAt);
    }

    private static MotorStatusPacket ParseLegacyPacket(ReadOnlySpan<byte> buffer, DateTime receivedAt)
    {
        return new MotorStatusPacket(
            HardwareErrorStatus: buffer[0],
            PresentTemperature: buffer[1],
            PresentInputVoltageRaw: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2)),
            PresentPosition: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2)),
            PresentVelocity: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2)),
            PresentCurrentRaw: (ushort)Math.Max(0, (int)BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(8, 2))),
            PresentPwm: (ushort)Math.Max(0, (int)BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(10, 2))),
            GoalPosition: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12, 2)),
            GoalVelocity: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14, 2)),
            Moving: buffer[16],
            MovingStatus: buffer[17],
            ReceivedAt: receivedAt);
    }

    private static int ResolvePort(int? port)
    {
        if (port is > 0 and <= 65535)
        {
            return port.Value;
        }

        var envPort = Environment.GetEnvironmentVariable("MOTOR_STATUS_PORT");
        return int.TryParse(envPort, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535
            ? parsedPort
            : DefaultPort;
    }
}
