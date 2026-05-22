using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenCvSharp;

var options = BenchmarkOptions.Parse(args);
Console.WriteLine("LIG GUI UDP receive benchmark");
Console.WriteLine($"EO port={options.EoPort}, IR port={options.IrPort}, seconds={options.Seconds:0.0}, warmup={options.WarmupSeconds:0.0}");
Console.WriteLine("Close the GUI before running this tool because it binds the same UDP ports.");
Console.WriteLine();

var split = await BenchmarkRunner.RunSplitAsync(options);
PrintModeResult("split receivers", split);

Console.WriteLine();
await Task.Delay(TimeSpan.FromSeconds(options.PauseSeconds));

var single = BenchmarkRunner.RunSingle(options);
PrintModeResult("single receiver", single);

Console.WriteLine();
PrintComparison(split, single);

static void PrintModeResult(string title, ModeResult result)
{
    Console.WriteLine($"[{title}] elapsed={result.Elapsed.TotalSeconds:0.0}s");
    Console.WriteLine("Stream  Frames  FPS    Packets  MB     Decode avg/max ms  Frame KB avg  Gaps  Fail  Incomplete");
    foreach (var stream in result.Streams)
    {
        Console.WriteLine(
            $"{stream.Name,-6} {stream.Frames,6} {stream.Fps,5:0.0} {stream.Packets,8} {stream.Megabytes,6:0.0} " +
            $"{stream.AverageProcessMs,8:0.00}/{stream.MaxProcessMs,-7:0.00} {stream.AverageFrameKb,12:0.0} " +
            $"{stream.FrameGaps,5} {stream.DecodeFailures,5} {stream.IncompleteFrames,10}");
    }
}

static void PrintComparison(ModeResult split, ModeResult single)
{
    Console.WriteLine("[difference: split - single]");
    Console.WriteLine("Stream  FPS diff  Decode avg diff ms  Packets diff  Frames diff");
    foreach (var splitStream in split.Streams)
    {
        var singleStream = single.Streams.First(s => s.Name == splitStream.Name);
        Console.WriteLine(
            $"{splitStream.Name,-6} {splitStream.Fps - singleStream.Fps,8:0.0} " +
            $"{splitStream.AverageProcessMs - singleStream.AverageProcessMs,18:0.00} " +
            $"{splitStream.Packets - singleStream.Packets,12} {splitStream.Frames - singleStream.Frames,11}");
    }
}

internal sealed record BenchmarkOptions(
    int EoPort,
    int IrPort,
    double Seconds,
    double WarmupSeconds,
    double PauseSeconds,
    bool IrFalseColor)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            options[key] = value;
        }

        return new BenchmarkOptions(
            GetInt(options, "eo-port", 6000),
            GetInt(options, "ir-port", 6001),
            GetDouble(options, "seconds", 20),
            GetDouble(options, "warmup", 3),
            GetDouble(options, "pause", 2),
            GetBool(options, "ir-false-color", true));
    }

    private static int GetInt(Dictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double GetDouble(Dictionary<string, string> options, string key, double fallback) =>
        options.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool GetBool(Dictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var value)
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            : fallback;
}

internal static class BenchmarkRunner
{
    public static async Task<ModeResult> RunSplitAsync(BenchmarkOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds + options.WarmupSeconds));
        var eo = new StreamReceiver("EO", options.EoPort, applyIrFalseColor: false, options.WarmupSeconds);
        var ir = new StreamReceiver("IR", options.IrPort, options.IrFalseColor, options.WarmupSeconds);
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            eo.RunAsync(cts.Token),
            ir.RunAsync(cts.Token));
        stopwatch.Stop();
        return new ModeResult(stopwatch.Elapsed, [eo.Snapshot(), ir.Snapshot()]);
    }

    public static ModeResult RunSingle(BenchmarkOptions options)
    {
        using var eo = StreamSocket.Bind("EO", options.EoPort, applyIrFalseColor: false, options.WarmupSeconds);
        using var ir = StreamSocket.Bind("IR", options.IrPort, options.IrFalseColor, options.WarmupSeconds);
        var sockets = new[] { eo, ir };
        var buffer = new byte[65535];
        var stopwatch = Stopwatch.StartNew();
        var endAt = stopwatch.Elapsed + TimeSpan.FromSeconds(options.Seconds + options.WarmupSeconds);

        while (stopwatch.Elapsed < endAt)
        {
            var readable = sockets.Select(s => s.Socket).ToList();
            Socket.Select(readable, null, null, 20_000);
            if (readable.Count == 0)
            {
                continue;
            }

            foreach (var socket in readable)
            {
                var stream = ReferenceEquals(socket, eo.Socket) ? eo : ir;
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try
                {
                    count = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException)
                {
                    continue;
                }

                var packet = new byte[count];
                Buffer.BlockCopy(buffer, 0, packet, 0, count);
                stream.Process(packet);
            }
        }

        stopwatch.Stop();
        return new ModeResult(stopwatch.Elapsed, [eo.Snapshot(), ir.Snapshot()]);
    }
}

internal sealed class StreamReceiver
{
    private readonly StreamProcessor _processor;
    private readonly UdpClient _client;

    public StreamReceiver(string name, int port, bool applyIrFalseColor, double warmupSeconds)
    {
        _processor = new StreamProcessor(name, applyIrFalseColor, warmupSeconds);
        _client = new UdpClient();
        _client.Client.ExclusiveAddressUse = false;
        _client.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(cancellationToken);
                _processor.Process(result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _client.Close();
            _client.Dispose();
        }
    }

    public StreamResult Snapshot() => _processor.Snapshot();
}

internal sealed class StreamSocket : IDisposable
{
    private readonly StreamProcessor _processor;

    private StreamSocket(string name, Socket socket, bool applyIrFalseColor, double warmupSeconds)
    {
        Name = name;
        Socket = socket;
        _processor = new StreamProcessor(name, applyIrFalseColor, warmupSeconds);
    }

    public string Name { get; }

    public Socket Socket { get; }

    public static StreamSocket Bind(string name, int port, bool applyIrFalseColor, double warmupSeconds)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return new StreamSocket(name, socket, applyIrFalseColor, warmupSeconds);
    }

    public void Process(byte[] packet) => _processor.Process(packet);

    public StreamResult Snapshot() => _processor.Snapshot();

    public void Dispose() => Socket.Dispose();
}

internal sealed class StreamProcessor
{
    private const int LegacyHeaderSize = 20;
    private const int SentinelImageHeaderSize = 15;
    private const int FragmentHeaderSize = 28;
    private static readonly byte[] SentinelPacketMagic = "SNTL"u8.ToArray();
    private static readonly byte[] ImageFragmentMagic = "IMGF"u8.ToArray();
    private static readonly byte[] DetectionPacketMagic = "DETS"u8.ToArray();
    private static readonly byte[] StatusPacketMagic = "STAT"u8.ToArray();

    private readonly string _name;
    private readonly bool _applyIrFalseColor;
    private readonly double _warmupSeconds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<FrameFragmentKey, ImageFragmentBuffer> _fragments = new();
    private readonly object _gate = new();
    private StreamCounters _counters;
    private uint? _lastFrameIndex;

    public StreamProcessor(string name, bool applyIrFalseColor, double warmupSeconds)
    {
        _name = name;
        _applyIrFalseColor = applyIrFalseColor;
        _warmupSeconds = warmupSeconds;
    }

    public void Process(byte[] packet)
    {
        lock (_gate)
        {
            if (_clock.Elapsed.TotalSeconds < _warmupSeconds)
            {
                ProcessPacket(packet, collect: false);
                return;
            }

            if (_counters.StartedAt == default)
            {
                _counters.StartedAt = DateTime.UtcNow;
            }

            _counters.Packets++;
            _counters.Bytes += packet.Length;
            ProcessPacket(packet, collect: true);
        }
    }

    public StreamResult Snapshot()
    {
        lock (_gate)
        {
            var elapsed = Math.Max(0.001, _clock.Elapsed.TotalSeconds - _warmupSeconds);
            var decodeAvg = _counters.Frames == 0 ? 0 : _counters.ProcessTicks / (double)_counters.Frames / Stopwatch.Frequency * 1000.0;
            var decodeMax = _counters.MaxProcessTicks / (double)Stopwatch.Frequency * 1000.0;
            var avgFrameKb = _counters.Frames == 0 ? 0 : _counters.FrameBytes / (double)_counters.Frames / 1024.0;

            return new StreamResult(
                _name,
                _counters.Frames,
                _counters.Frames / elapsed,
                _counters.Packets,
                _counters.Bytes / 1024.0 / 1024.0,
                decodeAvg,
                decodeMax,
                avgFrameKb,
                _counters.FrameGaps,
                _counters.DecodeFailures,
                _fragments.Count);
        }
    }

    private void ProcessPacket(byte[] packet, bool collect)
    {
        if (packet.Length == 0 ||
            StartsWith(packet, DetectionPacketMagic) ||
            (StartsWith(packet, SentinelPacketMagic) && packet.Length > 4 && packet[4] == 0x10) ||
            StartsWith(packet, StatusPacketMagic))
        {
            return;
        }

        if (TryReadFragment(packet, out var fragment))
        {
            var key = new FrameFragmentKey(fragment.StampNs, fragment.FrameIndex);
            if (!_fragments.TryGetValue(key, out var buffer) || !buffer.IsCompatibleWith(fragment))
            {
                buffer = new ImageFragmentBuffer(fragment);
                _fragments[key] = buffer;
            }

            buffer.Add(fragment);
            TrimFragmentBuffers();
            if (buffer.IsComplete)
            {
                _fragments.Remove(key);
                DecodeFrame(buffer.Assemble(), fragment.FrameIndex, collect);
            }

            return;
        }

        if (LooksLikeJpeg(packet))
        {
            DecodeFrame(packet, frameIndex: 0, collect);
            return;
        }

        if (packet.Length > LegacyHeaderSize)
        {
            var frameIndex = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
            var totalLength = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12, 4));
            if (totalLength > 0 && totalLength <= packet.Length - LegacyHeaderSize)
            {
                DecodeFrame(packet[LegacyHeaderSize..(LegacyHeaderSize + (int)totalLength)], frameIndex, collect);
            }
        }
    }

    private void DecodeFrame(byte[] encoded, uint frameIndex, bool collect)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var decoded = Cv2.ImDecode(encoded, ImreadModes.Color);
            if (decoded.Empty())
            {
                if (collect)
                {
                    _counters.DecodeFailures++;
                }

                return;
            }

            if (_applyIrFalseColor)
            {
                using var colorized = CreateIrFalseColorFrame(decoded);
            }

            stopwatch.Stop();
            if (!collect)
            {
                return;
            }

            _counters.Frames++;
            _counters.FrameBytes += encoded.Length;
            _counters.ProcessTicks += stopwatch.ElapsedTicks;
            _counters.MaxProcessTicks = Math.Max(_counters.MaxProcessTicks, stopwatch.ElapsedTicks);
            if (_lastFrameIndex is not null && frameIndex > _lastFrameIndex.Value + 1)
            {
                _counters.FrameGaps += frameIndex - _lastFrameIndex.Value - 1;
            }

            if (frameIndex != 0)
            {
                _lastFrameIndex = frameIndex;
            }
        }
        catch
        {
            if (collect)
            {
                _counters.DecodeFailures++;
            }
        }
    }

    private static Mat CreateIrFalseColorFrame(Mat source)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        var colorized = new Mat();
        Cv2.ApplyColorMap(gray, colorized, ColormapTypes.Jet);
        return colorized;
    }

    private static bool TryReadFragment(byte[] packet, out ImageFragmentPacket fragment)
    {
        fragment = default;
        if (TryReadSentinelFragment(packet, out fragment))
        {
            return true;
        }

        if (packet.Length < FragmentHeaderSize || !StartsWith(packet, ImageFragmentMagic))
        {
            return false;
        }

        var span = packet.AsSpan();
        fragment = new ImageFragmentPacket(
            BinaryPrimitives.ReadUInt64BigEndian(span[4..12]),
            BinaryPrimitives.ReadUInt32BigEndian(span[12..16]),
            BinaryPrimitives.ReadUInt32BigEndian(span[16..20]),
            BinaryPrimitives.ReadUInt16BigEndian(span[20..22]),
            BinaryPrimitives.ReadUInt16BigEndian(span[22..24]),
            BinaryPrimitives.ReadUInt16BigEndian(span[24..26]),
            BinaryPrimitives.ReadUInt16BigEndian(span[26..28]),
            packet[FragmentHeaderSize..]);
        return fragment.FragmentCount > 0 && fragment.FragmentIndex < fragment.FragmentCount;
    }

    private static bool TryReadSentinelFragment(byte[] packet, out ImageFragmentPacket fragment)
    {
        fragment = default;
        if (packet.Length < SentinelImageHeaderSize ||
            !StartsWith(packet, SentinelPacketMagic) ||
            packet[4] is not (0x01 or 0x02))
        {
            return false;
        }

        var span = packet.AsSpan();
        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(span[5..9]);
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(span[9..11]);
        var totalChunks = BinaryPrimitives.ReadUInt16LittleEndian(span[11..13]);
        var payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(span[13..15]);
        if (totalChunks == 0 ||
            chunkIndex >= totalChunks ||
            payloadSize == 0 ||
            packet.Length < SentinelImageHeaderSize + payloadSize)
        {
            return false;
        }

        fragment = new ImageFragmentPacket(
            0,
            frameId,
            0,
            0,
            0,
            chunkIndex,
            totalChunks,
            packet[SentinelImageHeaderSize..(SentinelImageHeaderSize + payloadSize)]);
        return true;
    }

    private void TrimFragmentBuffers()
    {
        if (_fragments.Count <= 64)
        {
            return;
        }

        foreach (var key in _fragments.OrderBy(pair => pair.Value.LastUpdatedTicks).Take(_fragments.Count - 64).Select(pair => pair.Key).ToArray())
        {
            _fragments.Remove(key);
        }
    }

    private static bool StartsWith(byte[] packet, byte[] prefix) =>
        packet.Length >= prefix.Length && packet.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool LooksLikeJpeg(byte[] packet) => packet.Length > 3 && packet[0] == 0xFF && packet[1] == 0xD8;
}

internal sealed record ModeResult(TimeSpan Elapsed, StreamResult[] Streams);

internal sealed record StreamResult(
    string Name,
    long Frames,
    double Fps,
    long Packets,
    double Megabytes,
    double AverageProcessMs,
    double MaxProcessMs,
    double AverageFrameKb,
    long FrameGaps,
    long DecodeFailures,
    int IncompleteFrames);

internal struct StreamCounters
{
    public DateTime StartedAt;
    public long Packets;
    public long Bytes;
    public long Frames;
    public long FrameBytes;
    public long ProcessTicks;
    public long MaxProcessTicks;
    public long FrameGaps;
    public long DecodeFailures;
}

internal readonly record struct FrameFragmentKey(ulong StampNs, uint FrameIndex);

internal readonly record struct ImageFragmentPacket(
    ulong StampNs,
    uint FrameIndex,
    uint TotalLength,
    ushort DeclaredWidth,
    ushort DeclaredHeight,
    ushort FragmentIndex,
    ushort FragmentCount,
    byte[] Payload);

internal sealed class ImageFragmentBuffer
{
    private readonly byte[]?[] _parts;

    public ImageFragmentBuffer(ImageFragmentPacket firstFragment)
    {
        StampNs = firstFragment.StampNs;
        FrameIndex = firstFragment.FrameIndex;
        TotalLength = firstFragment.TotalLength;
        DeclaredWidth = firstFragment.DeclaredWidth;
        DeclaredHeight = firstFragment.DeclaredHeight;
        FragmentCount = firstFragment.FragmentCount;
        _parts = new byte[FragmentCount][];
        LastUpdatedTicks = Environment.TickCount64;
    }

    public ulong StampNs { get; }

    public uint FrameIndex { get; }

    public uint TotalLength { get; }

    public ushort DeclaredWidth { get; }

    public ushort DeclaredHeight { get; }

    public ushort FragmentCount { get; }

    public int ReceivedCount { get; private set; }

    public long LastUpdatedTicks { get; private set; }

    public bool IsComplete => ReceivedCount == FragmentCount;

    public bool IsCompatibleWith(ImageFragmentPacket fragment)
    {
        return StampNs == fragment.StampNs &&
               FrameIndex == fragment.FrameIndex &&
               TotalLength == fragment.TotalLength &&
               DeclaredWidth == fragment.DeclaredWidth &&
               DeclaredHeight == fragment.DeclaredHeight &&
               FragmentCount == fragment.FragmentCount;
    }

    public bool Add(ImageFragmentPacket fragment)
    {
        if (!IsCompatibleWith(fragment))
        {
            return false;
        }

        var index = fragment.FragmentIndex;
        if (_parts[index] is not null)
        {
            LastUpdatedTicks = Environment.TickCount64;
            return false;
        }

        _parts[index] = fragment.Payload;
        ReceivedCount++;
        LastUpdatedTicks = Environment.TickCount64;
        return true;
    }

    public byte[] Assemble()
    {
        if (TotalLength == 0)
        {
            var outputBytes = new List<byte>();
            foreach (var part in _parts)
            {
                if (part is null)
                {
                    return [];
                }

                outputBytes.AddRange(part);
            }

            return outputBytes.ToArray();
        }

        var output = new byte[TotalLength];
        var offset = 0;
        foreach (var part in _parts)
        {
            if (part is null || offset + part.Length > output.Length)
            {
                return [];
            }

            Buffer.BlockCopy(part, 0, output, offset, part.Length);
            offset += part.Length;
        }

        return offset == output.Length ? output : [];
    }
}
