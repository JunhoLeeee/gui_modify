using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using BroadcastControl.App.Models.Motor;

namespace BroadcastControl.App.Services;

/// <summary>
/// GUI 모터 명령을 Jetson gui_bridge가 받는 UDP 패킷으로 전송합니다.
/// 모터 방향키/각도/속도 패킷과 위험 객체 추적 녹화 보조 패킷을 같은 서비스에서 관리합니다.
/// </summary>
public sealed class UdpMotorControlService : IDisposable
{
    private const string DefaultHost = "192.168.3.143";
    private const int DefaultPort = 8000;
    private const int DefaultTrackingRecordingControlPort = 8010;
    private const int TrackingRecordingPacketSize = 11;
    private static readonly byte[] TrackingRecordingPacketMagic = "TRCK"u8.ToArray();

    private readonly UdpClient _udpClient = new();
    private readonly object _endpointLock = new();
    private string _host;
    private int _port;
    private int _trackingRecordingControlPort;

    public UdpMotorControlService(string? host = null, int? port = null, int? trackingRecordingControlPort = null)
    {
        _host = ResolveHost(host);
        _port = ResolvePort(port);
        _trackingRecordingControlPort = ResolveTrackingRecordingControlPort(trackingRecordingControlPort);
    }

    public string Host
    {
        get
        {
            lock (_endpointLock)
            {
                return _host;
            }
        }
    }

    public int Port
    {
        get
        {
            lock (_endpointLock)
            {
                return _port;
            }
        }
    }

    public void ConfigureEndpoint(string? host, int? port = null, int? trackingRecordingControlPort = null)
    {
        lock (_endpointLock)
        {
            _host = ResolveHost(host);
            _port = ResolvePort(port);
            _trackingRecordingControlPort = ResolveTrackingRecordingControlPort(trackingRecordingControlPort);
        }
    }

    public bool TrySendMotorCommandPacket(
        byte mode,
        byte tracking,
        byte trackId,
        bool isEoPrimary,
        MotorButtonMask btnMask,
        ushort panPos,
        ushort tiltPos,
        byte scanStep,
        byte manualStep,
        out string? error)
    {
        var packet = MotorPacketSerializer.CreateCommandPacket(
            mode,
            tracking,
            trackId,
            isEoPrimary,
            btnMask,
            panPos,
            tiltPos,
            scanStep,
            manualStep);

        if (!TrySendPacket(packet, out error))
        {
            return false;
        }

        TrySendTrackingRecordingPacket(tracking != 0, isEoPrimary, trackId == 0xFF ? -1 : trackId);
        return true;
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }

    private bool TrySendPacket(byte[] packet, out string? error)
    {
        try
        {
            string host;
            int port;
            lock (_endpointLock)
            {
                host = _host;
                port = _port;
            }

            _udpClient.Send(packet, packet.Length, host, port);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void TrySendTrackingRecordingPacket(bool tracking, bool isEoPrimary, int yoloObjectId)
    {
        try
        {
            string host;
            int port;
            lock (_endpointLock)
            {
                host = _host;
                port = _trackingRecordingControlPort;
            }

            var packet = new byte[TrackingRecordingPacketSize];
            TrackingRecordingPacketMagic.CopyTo(packet, 0);
            packet[4] = tracking && yoloObjectId >= 0 ? (byte)1 : (byte)0;
            packet[5] = isEoPrimary ? (byte)1 : (byte)2;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6, 4), yoloObjectId);
            _udpClient.Send(packet, packet.Length, host, port);
        }
        catch
        {
            // 추적 녹화 패킷은 보조 기능이므로 실패해도 모터 명령 성공 여부에는 영향을 주지 않습니다.
        }
    }

    private static string ResolveHost(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host.Trim();
        }

        var envHost = Environment.GetEnvironmentVariable("MOTOR_CONTROL_HOST");
        return string.IsNullOrWhiteSpace(envHost)
            ? DefaultHost
            : envHost.Trim();
    }

    private static int ResolvePort(int? port)
    {
        if (port is > 0 and <= 65535)
        {
            return port.Value;
        }

        var envPort = Environment.GetEnvironmentVariable("MOTOR_CONTROL_PORT");
        return int.TryParse(envPort, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535
            ? parsedPort
            : DefaultPort;
    }

    private static int ResolveTrackingRecordingControlPort(int? port)
    {
        if (port is > 0 and <= 65535)
        {
            return port.Value;
        }

        var envPort = Environment.GetEnvironmentVariable("TRACKING_RECORDING_CONTROL_PORT");
        return int.TryParse(envPort, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535
            ? parsedPort
            : DefaultTrackingRecordingControlPort;
    }
}
