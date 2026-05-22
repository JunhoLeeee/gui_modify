// Jetson bridge가 보내는 EO/IR 영상 UDP 패킷을 수신하고 디코딩하는 서비스 파일이다.
// 영상 JPEG fragment를 조립하고, 같은 포트로 들어오는 detection/status 패킷을 파싱해 MainWindow 이벤트로 전달한다.
// GUI 내부 녹화 기능을 위해 현재 표시 상태의 영상 프레임을 OpenCV VideoWriter로 저장하는 기능도 포함한다.
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

namespace BroadcastControl.App.Services;

public sealed class UdpEncodedVideoReceiverService : IDisposable
{
    private const int DefaultPort = 6000;
    private const int SentinelImageHeaderSize = 15;
    private const int SentinelDetectionHeaderSize = 17;
    private const int SentinelDetectionRecordSize = 36;
    private const int SentinelTrackedDetectionRecordSize = 44;
    private const int LegacyHeaderSize = 20;
    private const int ImageFragmentHeaderSize = 28;
    private const int MaxImageFragmentBuffers = 32;
    private const int MetadataPacketSize = 36;
    private static readonly byte[] SentinelPacketMagic = "SNTL"u8.ToArray();
    private static readonly byte[] ImageFragmentMagic = "IMGF"u8.ToArray();
    private static readonly byte[] DetectionPacketMagic = "DETS"u8.ToArray();
    private static readonly byte[] StatusPacketMagic = "STAT"u8.ToArray();
    private static readonly JsonSerializerOptions PacketJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dispatcher _dispatcher;
    private readonly bool _applyIrFalseColor;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveLoopTask;
    private VideoWriter? _writer;
    private string? _recordingPath;
    private string? _recordingErrorMessage;
    private int _recordedFrameCount;
    private OpenCvSharp.Size _recordingOutputSize;
    private Mat? _latestRecordableFrame;
    private bool _isRecording;
    private double _brightness = 50;
    private double _contrast = 50;
    private double _zoomLevel = 1.0;
    private double _zoomPanX;
    private double _zoomPanY;
    private double _viewportWidth = 1;
    private double _viewportHeight = 1;
    private string? _lastSegmentSignature;
    private uint? _lastCycleIndex;
    private long _receivedPacketCount;
    private long _metadataPacketCount;
    private long _detectionPacketCount;
    private long _nonEmptyDetectionPacketCount;
    private long _decodeFailureCount;
    private long _unknownPacketCount;
    private readonly object _fragmentLock = new();
    private readonly Dictionary<FrameFragmentKey, ImageFragmentBuffer> _imageFragments = new();
    private readonly object _frameDispatchLock = new();
    private ReceivedVideoFrame? _pendingFrame;
    private bool _frameDispatchScheduled;

    public UdpEncodedVideoReceiverService(bool applyIrFalseColor = false)
    {
        _applyIrFalseColor = applyIrFalseColor;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public event Action<ReceivedVideoFrame>? FrameReady;
    public event Action<PlaybackSegmentInfo>? SegmentChanged;
    public event Action<PlaybackSegmentInfo>? SegmentLoopRestarted;
    public event Action<DetectionPacket>? DetectionsReceived;
    public event Action<YoloStatusPacket>? StatusReceived;
    public event Action<string>? DiagnosticsMessageReady;

    public int ListeningPort { get; private set; } = DefaultPort;

    public string? LastRecordingErrorMessage => _recordingErrorMessage;

    public int RecordedFrameCount => _recordedFrameCount;

    public bool Start(int port = DefaultPort)
    {
        if (_udpClient is not null)
        {
            return true;
        }

        try
        {
            // Windows GUI는 EO/IR 포트를 각각 열고 Jetson bridge가 보내는 UDP 패킷을 기다린다.
            // ReceiveBufferSize를 크게 잡아 짧은 시간에 여러 JPEG 청크가 몰려도 손실 가능성을 줄인다.
            ListeningPort = port;
            _udpClient = new UdpClient();
            _udpClient.Client.ExclusiveAddressUse = false;
            _udpClient.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            _lastSegmentSignature = null;
            _lastCycleIndex = null;
            _receivedPacketCount = 0;
            _metadataPacketCount = 0;
            _detectionPacketCount = 0;
            _nonEmptyDetectionPacketCount = 0;
            _decodeFailureCount = 0;
            _unknownPacketCount = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cancellationTokenSource.Token));
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
        _cancellationTokenSource?.Cancel();

        try
        {
            _udpClient?.Close();
        }
        catch
        {
        }

        _udpClient?.Dispose();
        _udpClient = null;

        try
        {
            _receiveLoopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _receiveLoopTask = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _lastSegmentSignature = null;
        _lastCycleIndex = null;
        _receivedPacketCount = 0;
        _metadataPacketCount = 0;
        _detectionPacketCount = 0;
        _nonEmptyDetectionPacketCount = 0;
        _decodeFailureCount = 0;
        _unknownPacketCount = 0;
        lock (_fragmentLock)
        {
            _imageFragments.Clear();
        }
        StopRecording();
    }

    public void SetBrightness(double value)
    {
        _brightness = Math.Clamp(value, 0, 100);
    }

    public void SetContrast(double value)
    {
        _contrast = Math.Clamp(value, 0, 100);
    }

    public void UpdateViewportTransform(double zoomLevel, double panX, double panY, double viewportWidth, double viewportHeight)
    {
        _zoomLevel = Math.Clamp(zoomLevel, 1.0, 4.0);
        _zoomPanX = panX;
        _zoomPanY = panY;
        _viewportWidth = Math.Max(viewportWidth, 1);
        _viewportHeight = Math.Max(viewportHeight, 1);
    }

    public string StartRecordingToDesktop()
    {
        if (_isRecording && !string.IsNullOrWhiteSpace(_recordingPath))
        {
            return _recordingPath;
        }

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
        {
            desktopPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop");
        }

        Directory.CreateDirectory(desktopPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _recordingPath = Path.Combine(desktopPath, $"video_{timestamp}.avi");
        _recordingErrorMessage = null;
        _recordedFrameCount = 0;
        _recordingOutputSize = default;
        _isRecording = true;

        if (_latestRecordableFrame is not null && !_latestRecordableFrame.Empty())
        {
            using var initialFrame = CreateRecordedFrame(_latestRecordableFrame);
            EnsureVideoWriter(initialFrame.Width, initialFrame.Height);
            WriteRecordingFrame(initialFrame);
        }

        return _recordingPath;
    }

    public string? StopRecording()
    {
        _isRecording = false;
        _writer?.Release();
        _writer?.Dispose();
        _writer = null;

        var savedPath = _recordingPath;
        _recordingPath = null;
        return savedPath;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // UDP 수신은 UI 스레드를 막으면 안 되므로 백그라운드 Task에서 계속 돌린다.
        // 실제 UI 갱신은 Dispatcher를 통해 안전하게 메인 스레드로 넘긴다.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_udpClient is null)
                {
                    break;
                }

                var receiveResult = await _udpClient.ReceiveAsync(cancellationToken);
                TryProcessPacket(receiveResult.Buffer, receiveResult.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private void TryProcessPacket(byte[] packet, IPEndPoint? remoteEndPoint)
    {
        if (packet.Length == 0)
        {
            return;
        }

        _receivedPacketCount++;
        if (_receivedPacketCount == 1)
        {
            var sourceText = remoteEndPoint is null
                ? "unknown source"
                : $"{remoteEndPoint.Address}:{remoteEndPoint.Port}";
            PublishDiagnosticMessage($"MEVA UDP 첫 패킷을 수신했습니다. 송신지: {sourceText}, 패킷 크기: {packet.Length} bytes");
        }

        // 패킷 종류는 magic/type으로 구분한다.
        // 오래된 IMGF/DETS 포맷과 현재 SNTL 포맷을 함께 지원해 실험 중 포맷 변경에도 GUI가 바로 죽지 않게 한다.
        if (TryExtractMetadataPacket(packet, out var segmentInfo))
        {
            _metadataPacketCount++;
            if (_metadataPacketCount == 1)
            {
                PublishDiagnosticMessage("MEVA UDP 구간 메타데이터 패킷을 수신했습니다.");
            }

            NotifySegmentChanged(segmentInfo);
            return;
        }

        if (TryExtractDetectionPacket(packet, out var detectionPacket))
        {
            _detectionPacketCount++;
            if (_detectionPacketCount == 1)
            {
                PublishDiagnosticMessage(
                    $"MEVA DETS packet received. frameId={detectionPacket.FrameId}, objects={detectionPacket.Detections.Count}, bytes={packet.Length}");
            }

            if (detectionPacket.Detections.Count > 0)
            {
                _nonEmptyDetectionPacketCount++;
                if (_nonEmptyDetectionPacketCount == 1 || _nonEmptyDetectionPacketCount % 20 == 0)
                {
                    PublishDiagnosticMessage(
                        $"MEVA non-empty DETS packet received. frameId={detectionPacket.FrameId}, objects={detectionPacket.Detections.Count}, count={_nonEmptyDetectionPacketCount}");
                }
            }

            _dispatcher.BeginInvoke(() => DetectionsReceived?.Invoke(detectionPacket));
            return;
        }

        if (TryExtractSentinelDetectionPacket(packet, out var sentinelDetectionPacket))
        {
            _detectionPacketCount++;
            if (sentinelDetectionPacket.Detections.Count > 0)
            {
                _nonEmptyDetectionPacketCount++;
            }

            _dispatcher.BeginInvoke(() => DetectionsReceived?.Invoke(sentinelDetectionPacket));
            return;
        }

        if (TryExtractStatusPacket(packet, out var statusPacket))
        {
            _dispatcher.BeginInvoke(() => StatusReceived?.Invoke(statusPacket));
            return;
        }

        if (TryExtractImageFragmentPacket(packet, out var fragmentPacket))
        {
            if (TryAssembleImageFragment(fragmentPacket, out var assembledFrame))
            {
                var decoded = TryDecodeFrame(
                    assembledFrame.EncodedBytes,
                    assembledFrame.DeclaredWidth,
                    assembledFrame.DeclaredHeight,
                    assembledFrame.FrameStampNs,
                    assembledFrame.FrameIndex,
                    null);
                if (!decoded)
                {
                    _decodeFailureCount++;
                    if (_decodeFailureCount == 1 || _decodeFailureCount % 20 == 0)
                    {
                        PublishDiagnosticMessage(
                            $"Fragmented UDP image decode failed. failures={_decodeFailureCount}, bytes={assembledFrame.EncodedBytes.Length}");
                    }
                }
            }

            return;
        }

        if (TryExtractSentinelImageFragmentPacket(packet, out var sentinelFragmentPacket))
        {
            if (TryAssembleImageFragment(sentinelFragmentPacket, out var assembledFrame))
            {
                var decoded = TryDecodeFrame(
                    assembledFrame.EncodedBytes,
                    assembledFrame.DeclaredWidth,
                    assembledFrame.DeclaredHeight,
                    assembledFrame.FrameStampNs,
                    assembledFrame.FrameIndex,
                    null);
                if (!decoded)
                {
                    _decodeFailureCount++;
                }
            }

            return;
        }

        if (LooksLikeJpeg(packet))
        {
            var decoded = TryDecodeFrame(packet, 0, 0, 0, 0, null);
            if (!decoded)
            {
                _decodeFailureCount++;
                if (_decodeFailureCount == 1 || _decodeFailureCount % 20 == 0)
                {
                    PublishDiagnosticMessage(
                        $"MEVA UDP JPEG 패킷은 도착했지만 화면 디코딩에 실패했습니다. 실패 횟수: {_decodeFailureCount}, 최근 패킷 크기: {packet.Length} bytes");
                }
            }

            return;
        }

        if (packet.Length <= LegacyHeaderSize)
        {
            _unknownPacketCount++;
            if (_unknownPacketCount == 1 || _unknownPacketCount % 20 == 0)
            {
                PublishDiagnosticMessage(
                    $"MEVA UDP 패킷 형식을 해석하지 못했습니다. 미인식 횟수: {_unknownPacketCount}, 최근 패킷 크기: {packet.Length} bytes");
            }
            return;
        }

        if (TryExtractEncodedFrame(packet, useBigEndian: true, out var bigEndianFrame))
        {
            var decoded = TryDecodeFrame(
                bigEndianFrame.EncodedBytes,
                bigEndianFrame.DeclaredWidth,
                bigEndianFrame.DeclaredHeight,
                bigEndianFrame.FrameStampNs,
                bigEndianFrame.FrameIndex,
                null);
            if (!decoded)
            {
                _decodeFailureCount++;
                if (_decodeFailureCount == 1 || _decodeFailureCount % 20 == 0)
                {
                    PublishDiagnosticMessage(
                        $"MEVA legacy big-endian 패킷 디코딩에 실패했습니다. 실패 횟수: {_decodeFailureCount}");
                }
            }
            return;
        }

        if (TryExtractEncodedFrame(packet, useBigEndian: false, out var littleEndianFrame))
        {
            var decoded = TryDecodeFrame(
                littleEndianFrame.EncodedBytes,
                littleEndianFrame.DeclaredWidth,
                littleEndianFrame.DeclaredHeight,
                littleEndianFrame.FrameStampNs,
                littleEndianFrame.FrameIndex,
                null);
            if (!decoded)
            {
                _decodeFailureCount++;
                if (_decodeFailureCount == 1 || _decodeFailureCount % 20 == 0)
                {
                    PublishDiagnosticMessage(
                        $"MEVA legacy little-endian 패킷 디코딩에 실패했습니다. 실패 횟수: {_decodeFailureCount}");
                }
            }

            return;
        }

        _unknownPacketCount++;
        if (_unknownPacketCount == 1 || _unknownPacketCount % 20 == 0)
        {
            PublishDiagnosticMessage(
                $"MEVA UDP 패킷 형식을 해석하지 못했습니다. 미인식 횟수: {_unknownPacketCount}, 최근 패킷 크기: {packet.Length} bytes");
        }
    }

    private bool TryDecodeFrame(
        byte[] encodedFrame,
        ushort declaredWidth,
        ushort declaredHeight,
        ulong frameStampNs,
        uint frameIndex,
        PlaybackSegmentInfo? segmentInfo)
    {
        try
        {
            // bridge는 대역폭을 줄이기 위해 프레임을 JPEG로 보내므로 OpenCV로 먼저 Mat로 복원한다.
            using var decoded = Cv2.ImDecode(encodedFrame, ImreadModes.Color);
            if (decoded.Empty())
            {
                return false;
            }

            if (declaredWidth > 0 && declaredHeight > 0 &&
                (decoded.Width != declaredWidth || decoded.Height != declaredHeight))
            {
            }

            // IR 화면은 옵션에 따라 grayscale 원본을 false color로 바꿔 온도 차이가 더 잘 보이게 한다.
            using var falseColorFrame = _applyIrFalseColor ? CreateIrFalseColorFrame(decoded) : new Mat();
            var displaySource = _applyIrFalseColor ? falseColorFrame : decoded;

            using var adjusted = new Mat();
            var alpha = 0.5 + (_contrast / 100.0);
            var beta = (_brightness - 50.0) * 2.0;
            displaySource.ConvertTo(adjusted, MatType.CV_8UC3, alpha, beta);

            _latestRecordableFrame?.Dispose();
            _latestRecordableFrame = adjusted.Clone();

            if (_isRecording)
            {
                using var recordingFrame = CreateRecordedFrame(adjusted);
                EnsureVideoWriter(recordingFrame.Width, recordingFrame.Height);
                WriteRecordingFrame(recordingFrame);
            }

            using var upscaledDisplay = _applyIrFalseColor ? CreateIrUpscaledDisplayFrame(adjusted) : new Mat();
            var displayFrame = upscaledDisplay.Empty() ? adjusted : upscaledDisplay;

            using var converted = new Mat();
            Cv2.CvtColor(displayFrame, converted, ColorConversionCodes.BGR2BGRA);

            var bufferSize = checked((int)(converted.Step() * converted.Rows));
            var stride = checked((int)converted.Step());

            var bitmap = BitmapSource.Create(
                converted.Width,
                converted.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                converted.Data,
                bufferSize,
                stride);

            bitmap.Freeze();
            NotifySegmentChanged(segmentInfo);
            var receivedFrame = new ReceivedVideoFrame(
                frameStampNs,
                frameIndex,
                declaredWidth > 0 ? declaredWidth : checked((ushort)decoded.Width),
                declaredHeight > 0 ? declaredHeight : checked((ushort)decoded.Height),
                bitmap);
            // 수신 속도가 UI 갱신 속도보다 빠를 수 있으므로 가장 최신 프레임만 큐에 남긴다.
            QueueLatestFrame(receivedFrame);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void QueueLatestFrame(ReceivedVideoFrame frame)
    {
        lock (_frameDispatchLock)
        {
            _pendingFrame = frame;
            if (_frameDispatchScheduled)
            {
                return;
            }

            _frameDispatchScheduled = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DrainLatestFrame));
    }

    private void DrainLatestFrame()
    {
        while (true)
        {
            ReceivedVideoFrame? frameToPublish;
            lock (_frameDispatchLock)
            {
                frameToPublish = _pendingFrame;
                _pendingFrame = null;
                if (frameToPublish is null)
                {
                    _frameDispatchScheduled = false;
                    return;
                }
            }

            FrameReady?.Invoke(frameToPublish.Value);
        }
    }

    private static Mat CreateIrFalseColorFrame(Mat source)
    {
        using var gray = new Mat();
        if (source.Channels() == 3)
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        }
        else if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        }
        else
        {
            source.CopyTo(gray);
        }

        using var gray8 = new Mat();
        if (gray.Type() == MatType.CV_8UC1)
        {
            gray.CopyTo(gray8);
        }
        else
        {
            Cv2.Normalize(gray, gray8, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        }

        // IR false-color view:
        var colorized = new Mat();
        Cv2.ApplyColorMap(gray8, colorized, ColormapTypes.Jet);
        return colorized;

        //var grayscale = new Mat();
        //Cv2.CvtColor(gray8, grayscale, ColorConversionCodes.GRAY2BGR);
        //return grayscale;
    }

    private Mat CreateIrUpscaledDisplayFrame(Mat source)
    {
        if (source.Empty())
        {
            return new Mat();
        }

        var targetWidth = Math.Max(source.Width, (int)Math.Round(_viewportWidth));
        var targetHeight = Math.Max(source.Height, (int)Math.Round(_viewportHeight));
        var scale = Math.Max(
            targetWidth / (double)source.Width,
            targetHeight / (double)source.Height);

        if (scale <= 1.05)
        {
            return new Mat();
        }

        var outputWidth = Math.Clamp((int)Math.Round(source.Width * scale), source.Width, 1920);
        var outputHeight = Math.Clamp((int)Math.Round(source.Height * scale), source.Height, 1080);

        if (outputWidth == source.Width && outputHeight == source.Height)
        {
            return new Mat();
        }

        var upscaled = new Mat();
        Cv2.Resize(
            source,
            upscaled,
            new OpenCvSharp.Size(outputWidth, outputHeight),
            0,
            0,
            InterpolationFlags.Lanczos4);
        return upscaled;
    }

    private Mat CreateRecordedFrame(Mat source)
    {
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var viewportAspect = _viewportWidth / _viewportHeight;
        var sourceAspect = (double)sourceWidth / sourceHeight;

        double baseX;
        double baseY;
        double baseWidth;
        double baseHeight;

        if (sourceAspect > viewportAspect)
        {
            baseHeight = sourceHeight;
            baseWidth = sourceHeight * viewportAspect;
            baseX = (sourceWidth - baseWidth) / 2.0;
            baseY = 0;
        }
        else
        {
            baseWidth = sourceWidth;
            baseHeight = sourceWidth / viewportAspect;
            baseX = 0;
            baseY = (sourceHeight - baseHeight) / 2.0;
        }

        var cropWidth = baseWidth / _zoomLevel;
        var cropHeight = baseHeight / _zoomLevel;
        var maxPanX = (_viewportWidth * (_zoomLevel - 1.0)) / 2.0;
        var maxPanY = (_viewportHeight * (_zoomLevel - 1.0)) / 2.0;

        var remainingX = baseWidth - cropWidth;
        var remainingY = baseHeight - cropHeight;
        var offsetX = GetViewportOffset(_zoomPanX, maxPanX, remainingX);
        var offsetY = GetViewportOffset(_zoomPanY, maxPanY, remainingY);

        var cropRect = new Rect(
            (int)Math.Clamp(Math.Round(baseX + offsetX), 0, sourceWidth - 1),
            (int)Math.Clamp(Math.Round(baseY + offsetY), 0, sourceHeight - 1),
            Math.Max(1, (int)Math.Clamp(Math.Round(cropWidth), 1, sourceWidth)),
            Math.Max(1, (int)Math.Clamp(Math.Round(cropHeight), 1, sourceHeight)));

        if (cropRect.X + cropRect.Width > sourceWidth)
        {
            cropRect.Width = sourceWidth - cropRect.X;
        }

        if (cropRect.Y + cropRect.Height > sourceHeight)
        {
            cropRect.Height = sourceHeight - cropRect.Y;
        }

        using var cropped = new Mat(source, cropRect);
        var output = new Mat();
        Cv2.Resize(
            cropped,
            output,
            new OpenCvSharp.Size(
                Math.Max(1, (int)Math.Round(baseWidth)),
                Math.Max(1, (int)Math.Round(baseHeight))));
        return output;
    }

    private static double GetViewportOffset(double pan, double maxPan, double remainingSize)
    {
        if (maxPan <= 0 || remainingSize <= 0)
        {
            return remainingSize / 2.0;
        }

        var normalized = (pan + maxPan) / (maxPan * 2.0);
        return (1.0 - normalized) * remainingSize;
    }

    private void EnsureVideoWriter(int width, int height)
    {
        if (_writer is not null || string.IsNullOrWhiteSpace(_recordingPath))
        {
            return;
        }

        _recordingOutputSize = NormalizeRecordingSize(width, height);
        var attempts = new List<string>();
        var strategies = new[]
        {
            (Api: VideoCaptureAPIs.OPENCV_MJPEG, Codec: FourCC.MJPG, Name: "OPENCV_MJPEG/MJPG"),
            (Api: VideoCaptureAPIs.MSMF, Codec: FourCC.MJPG, Name: "MSMF/MJPG"),
            (Api: VideoCaptureAPIs.FFMPEG, Codec: FourCC.MJPG, Name: "FFMPEG/MJPG"),
            (Api: VideoCaptureAPIs.ANY, Codec: FourCC.MJPG, Name: "ANY/MJPG"),
            (Api: VideoCaptureAPIs.MSMF, Codec: FourCC.XVID, Name: "MSMF/XVID"),
            (Api: VideoCaptureAPIs.FFMPEG, Codec: FourCC.XVID, Name: "FFMPEG/XVID"),
            (Api: VideoCaptureAPIs.ANY, Codec: FourCC.XVID, Name: "ANY/XVID"),
        };

        foreach (var strategy in strategies)
        {
            VideoWriter? writer = null;
            try
            {
                writer = new VideoWriter(
                    _recordingPath,
                    strategy.Api,
                    strategy.Codec,
                    30,
                    _recordingOutputSize,
                    true);

                if (writer.IsOpened())
                {
                    _writer = writer;
                    _recordingErrorMessage = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                attempts.Add($"{strategy.Name}: {ex.Message}");
            }
            finally
            {
                if (writer is not null && _writer is null)
                {
                    writer.Dispose();
                }
            }

            attempts.Add(strategy.Name);
        }

        _recordingErrorMessage = "Could not open a video writer for the desktop recording path. Tried: "
            + string.Join(", ", attempts);
    }

    private static OpenCvSharp.Size NormalizeRecordingSize(int width, int height)
    {
        var normalizedWidth = Math.Max(2, width);
        var normalizedHeight = Math.Max(2, height);

        if ((normalizedWidth & 1) != 0)
        {
            normalizedWidth--;
        }

        if ((normalizedHeight & 1) != 0)
        {
            normalizedHeight--;
        }

        return new OpenCvSharp.Size(normalizedWidth, normalizedHeight);
    }

    private void WriteRecordingFrame(Mat frame)
    {
        if (_writer is null || !_writer.IsOpened())
        {
            return;
        }

        if (frame.Size() == _recordingOutputSize)
        {
            _writer.Write(frame);
        }
        else
        {
            using var resizedFrame = new Mat();
            Cv2.Resize(frame, resizedFrame, _recordingOutputSize);
            _writer.Write(resizedFrame);
        }

        _recordedFrameCount++;
    }

    public void Dispose()
    {
        Stop();
        _latestRecordableFrame?.Dispose();
        _latestRecordableFrame = null;
    }

    private static bool LooksLikeJpeg(IReadOnlyList<byte> packet)
    {
        return packet.Count >= 4
            && packet[0] == 0xFF
            && packet[1] == 0xD8
            && packet[^2] == 0xFF
            && packet[^1] == 0xD9;
    }

    private void NotifySegmentChanged(PlaybackSegmentInfo? segmentInfo)
    {
        if (!segmentInfo.HasValue)
        {
            return;
        }

        var signature = segmentInfo.Value.GetSignature();
        if (!string.Equals(_lastSegmentSignature, signature, StringComparison.Ordinal))
        {
            _lastSegmentSignature = signature;
            _lastCycleIndex = segmentInfo.Value.CycleIndex;
            _dispatcher.BeginInvoke(() => SegmentChanged?.Invoke(segmentInfo.Value));
            return;
        }

        if (_lastCycleIndex.HasValue &&
            segmentInfo.Value.CycleIndex > _lastCycleIndex.Value)
        {
            _lastCycleIndex = segmentInfo.Value.CycleIndex;
            _dispatcher.BeginInvoke(() => SegmentLoopRestarted?.Invoke(segmentInfo.Value));
            return;
        }

        _lastCycleIndex = segmentInfo.Value.CycleIndex;
    }

    private void PublishDiagnosticMessage(string message)
    {
        _dispatcher.BeginInvoke(() => DiagnosticsMessageReady?.Invoke(message));
    }

    private static bool TryExtractMetadataPacket(byte[] packet, out PlaybackSegmentInfo segmentInfo)
    {
        segmentInfo = default;

        if (packet.Length != MetadataPacketSize)
        {
            return false;
        }

        var header = packet.AsSpan(0, MetadataPacketSize);
        if (header[0] != (byte)'M' ||
            header[1] != (byte)'E' ||
            header[2] != (byte)'V' ||
            header[3] != (byte)'A')
        {
            return false;
        }

        var imageByteLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        var declaredWidth = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2));
        var declaredHeight = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(10, 2));
        var clipIndex = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4));
        var clipCount = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var segmentStartSeconds = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        var segmentEndSeconds = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(24, 4));
        var currentPlaybackSeconds = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(28, 4));
        var cycleIndex = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(32, 4));

        if (imageByteLength != 0)
        {
            return false;
        }

        if (declaredWidth != 0 || declaredHeight != 0)
        {
            return false;
        }

        segmentInfo = new PlaybackSegmentInfo(
            clipIndex,
            clipCount,
            segmentStartSeconds,
            segmentEndSeconds,
            currentPlaybackSeconds,
            cycleIndex);
        return true;
    }

    private static bool TryExtractEncodedFrame(byte[] packet, bool useBigEndian, out EncodedFrame frame)
    {
        frame = default;

        if (packet.Length <= LegacyHeaderSize)
        {
            return false;
        }

        var header = packet.AsSpan(0, LegacyHeaderSize);
        var payload = packet.AsSpan(LegacyHeaderSize);
        var frameStampNs = useBigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(header.Slice(0, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0, 8));
        var frameIndex = useBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));

        var imageByteLength = useBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));

        var declaredWidth = useBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(16, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(16, 2));

        var declaredHeight = useBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(18, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(18, 2));

        if (imageByteLength == 0 || imageByteLength > payload.Length)
        {
            return false;
        }

        if ((declaredWidth == 0) != (declaredHeight == 0))
        {
            return false;
        }

        if (declaredWidth > 10000 || declaredHeight > 10000)
        {
            return false;
        }

        var encodedBytes = payload[..checked((int)imageByteLength)].ToArray();
        if (!LooksLikeJpeg(encodedBytes))
        {
            return false;
        }

        frame = new EncodedFrame(encodedBytes, declaredWidth, declaredHeight, frameStampNs, frameIndex);
        return true;
    }

    private static bool TryExtractImageFragmentPacket(byte[] packet, out ImageFragmentPacket fragment)
    {
        fragment = default;

        if (!HasPacketMagic(packet, ImageFragmentMagic) || packet.Length <= ImageFragmentHeaderSize)
        {
            return false;
        }

        var header = packet.AsSpan(0, ImageFragmentHeaderSize);
        var stampNs = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(4, 8));
        var frameIndex = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4));
        var totalLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var declaredWidth = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(20, 2));
        var declaredHeight = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(22, 2));
        var fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(24, 2));
        var fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(26, 2));

        if (totalLength == 0 ||
            totalLength > 16 * 1024 * 1024 ||
            fragmentCount == 0 ||
            fragmentIndex >= fragmentCount ||
            declaredWidth == 0 ||
            declaredHeight == 0)
        {
            return false;
        }

        var payload = packet[ImageFragmentHeaderSize..];
        if (payload.Length == 0)
        {
            return false;
        }

        fragment = new ImageFragmentPacket(
            stampNs,
            frameIndex,
            totalLength,
            declaredWidth,
            declaredHeight,
            fragmentIndex,
            fragmentCount,
            payload);
        return true;
    }

    private static bool TryExtractSentinelImageFragmentPacket(byte[] packet, out ImageFragmentPacket fragment)
    {
        fragment = default;

        // SNTL 영상 패킷은 15B 헤더 뒤에 JPEG 조각이 붙는다.
        // frame_id, chunk_idx, total_chunks를 이용해 한 장의 JPEG로 다시 합친다.
        if (!HasPacketMagic(packet, SentinelPacketMagic) || packet.Length < SentinelImageHeaderSize)
        {
            return false;
        }

        var packetType = packet[4];
        if (packetType is not (0x01 or 0x02))
        {
            return false;
        }

        var header = packet.AsSpan(0, SentinelImageHeaderSize);
        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(5, 4));
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(9, 2));
        var totalChunks = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(11, 2));
        var payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(13, 2));

        if (totalChunks == 0 ||
            chunkIndex >= totalChunks ||
            payloadSize == 0 ||
            packet.Length < SentinelImageHeaderSize + payloadSize)
        {
            return false;
        }

        var payload = packet.AsSpan(SentinelImageHeaderSize, payloadSize).ToArray();
        fragment = new ImageFragmentPacket(
            0,
            frameId,
            0,
            0,
            0,
            chunkIndex,
            totalChunks,
            payload);
        return true;
    }

    private bool TryAssembleImageFragment(ImageFragmentPacket fragment, out EncodedFrame frame)
    {
        frame = default;

        lock (_fragmentLock)
        {
            // UDP는 순서 보장과 재전송이 없기 때문에 청크를 잠시 보관했다가 모두 모였을 때만 디코딩한다.
            // 오래된 조각은 CleanupStaleImageFragments에서 제거해 메모리가 계속 늘어나는 것을 막는다.
            CleanupStaleImageFragments();
            var key = new FrameFragmentKey(fragment.StampNs, fragment.FrameIndex);
            if (!_imageFragments.TryGetValue(key, out var buffer) || !buffer.IsCompatibleWith(fragment))
            {
                buffer = new ImageFragmentBuffer(fragment);
                _imageFragments[key] = buffer;
            }

            if (!buffer.Add(fragment))
            {
                return false;
            }

            if (!buffer.IsComplete)
            {
                return false;
            }

            _imageFragments.Remove(key);
            var encodedBytes = buffer.Assemble();
            if (encodedBytes.Length == 0 || !LooksLikeJpeg(encodedBytes))
            {
                return false;
            }

            frame = new EncodedFrame(
                encodedBytes,
                fragment.DeclaredWidth,
                fragment.DeclaredHeight,
                fragment.StampNs,
                fragment.FrameIndex);
            return true;
        }
    }

    private void CleanupStaleImageFragments()
    {
        var now = Environment.TickCount64;
        var staleKeys = _imageFragments
            .Where(pair => now - pair.Value.LastUpdatedTicks > 2_000)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in staleKeys)
        {
            _imageFragments.Remove(key);
        }

        if (_imageFragments.Count <= MaxImageFragmentBuffers)
        {
            return;
        }

        var overflowKeys = _imageFragments
            .OrderBy(pair => pair.Value.LastUpdatedTicks)
            .Take(_imageFragments.Count - MaxImageFragmentBuffers)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in overflowKeys)
        {
            _imageFragments.Remove(key);
        }
    }

    private static bool TryExtractDetectionPacket(byte[] packet, out DetectionPacket detectionPacket)
    {
        detectionPacket = default;

        if (!HasPacketMagic(packet, DetectionPacketMagic))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(packet, DetectionPacketMagic.Length, packet.Length - DetectionPacketMagic.Length);
            var payload = JsonSerializer.Deserialize<DetectionPacketPayload>(json, PacketJsonOptions);
            if (payload is null)
            {
                return false;
            }

            var detections = (payload.Detections ?? [])
                .Select(d => new DetectionInfo(
                    d.ClassName ?? "unknown",
                    d.Score,
                    d.X1,
                    d.Y1,
                    d.X2,
                    d.Y2,
                    d.ObjectId))
                .ToArray();

            detectionPacket = new DetectionPacket(
                payload.StampNs,
                payload.FrameId,
                payload.Width,
                payload.Height,
                detections);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractSentinelDetectionPacket(byte[] packet, out DetectionPacket detectionPacket)
    {
        detectionPacket = default;

        if (!HasPacketMagic(packet, SentinelPacketMagic) ||
            packet.Length < SentinelDetectionHeaderSize + 2 ||
            packet[4] is not (0x10 or 0x11))
        {
            return false;
        }

        try
        {
            var stream = packet[4] == 0x11 ? DetectionStream.Ir : DetectionStream.Eo;
            var frameId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5, 4));
            var stampSec = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(9, 4));
            var stampNsec = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(13, 4));
            var stampNs = ((ulong)stampSec * 1_000_000_000UL) + stampNsec;
            var offset = SentinelDetectionHeaderSize;
            var detections = new List<DetectionInfo>();

            if (!TryReadUInt16(packet, ref offset, out var firstCount))
            {
                return false;
            }

            if (packet.Length == offset + firstCount * SentinelTrackedDetectionRecordSize)
            {
                for (var index = 0; index < firstCount; index++)
                {
                    if (!TryReadTrackedDetection(packet, ref offset, out var detection))
                    {
                        return false;
                    }

                    detections.Add(detection);
                }

                detectionPacket = new DetectionPacket(stampNs, frameId, 0, 0, detections, stream);
                return true;
            }

            offset = SentinelDetectionHeaderSize + 2;
            for (var index = 0; index < firstCount; index++)
            {
                if (packet.Length < offset + SentinelDetectionRecordSize)
                {
                    return false;
                }

                var className = ReadFixedUtf8(packet.AsSpan(offset, 16));
                var score = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 16, 4));
                var x1 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 20, 4));
                var y1 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 24, 4));
                var x2 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 28, 4));
                var y2 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 32, 4));
                detections.Add(new DetectionInfo(className, score, x1, y1, x2, y2, index + 1));
                offset += SentinelDetectionRecordSize;
            }

            if (!TryReadUInt16(packet, ref offset, out var trackedCount))
            {
                return false;
            }

            for (var index = 0; index < trackedCount; index++)
            {
                if (!TryReadTrackedDetection(packet, ref offset, out var detection))
                {
                    return false;
                }

                detections.Add(detection);
            }

            detectionPacket = new DetectionPacket(stampNs, frameId, 0, 0, detections, stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadTrackedDetection(byte[] packet, ref int offset, out DetectionInfo detection)
    {
        detection = default;
        if (packet.Length < offset + SentinelTrackedDetectionRecordSize)
        {
            return false;
        }

        var trackId = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
        var className = ReadFixedUtf8(packet.AsSpan(offset + 8, 16));
        var score = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 24, 4));
        var x1 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 28, 4));
        var y1 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 32, 4));
        var x2 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 36, 4));
        var y2 = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 40, 4));
        detection = new DetectionInfo(className, score, x1, y1, x2, y2, trackId);
        offset += SentinelTrackedDetectionRecordSize;
        return true;
    }

    private static bool TryReadUInt16(byte[] packet, ref int offset, out ushort value)
    {
        value = 0;
        if (packet.Length < offset + 2)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset, 2));
        offset += 2;
        return true;
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        var text = Encoding.UTF8.GetString(bytes[..length]).Trim();
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }

    private static bool TryExtractStatusPacket(byte[] packet, out YoloStatusPacket statusPacket)
    {
        statusPacket = default;

        if (!HasPacketMagic(packet, StatusPacketMagic))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(packet, StatusPacketMagic.Length, packet.Length - StatusPacketMagic.Length);
            var payload = JsonSerializer.Deserialize<YoloStatusPacketPayload>(json, PacketJsonOptions);
            if (payload is null)
            {
                return false;
            }

            statusPacket = new YoloStatusPacket(
                payload.Enabled,
                payload.ModelLoaded,
                payload.ConfThreshold,
                payload.LastError ?? string.Empty,
                payload.Source ?? string.Empty,
                payload.StampNs,
                payload.FrameId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPacketMagic(byte[] packet, byte[] magic)
    {
        if (packet.Length <= magic.Length)
        {
            return false;
        }

        return packet.AsSpan(0, magic.Length).SequenceEqual(magic);
    }

    private readonly record struct EncodedFrame(
        byte[] EncodedBytes,
        ushort DeclaredWidth,
        ushort DeclaredHeight,
        ulong FrameStampNs,
        uint FrameIndex);

    private readonly record struct FrameFragmentKey(ulong StampNs, uint FrameIndex);

    private readonly record struct ImageFragmentPacket(
        ulong StampNs,
        uint FrameIndex,
        uint TotalLength,
        ushort DeclaredWidth,
        ushort DeclaredHeight,
        ushort FragmentIndex,
        ushort FragmentCount,
        byte[] Payload);

    private sealed class ImageFragmentBuffer
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
                var dynamicOutput = new List<byte>();
                foreach (var part in _parts)
                {
                    if (part is null)
                    {
                        return [];
                    }

                    dynamicOutput.AddRange(part);
                }

                return dynamicOutput.ToArray();
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

    private sealed record DetectionPacketPayload
    {
        public ulong StampNs { get; init; }
        public uint FrameId { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public DetectionInfoPayload[]? Detections { get; init; }
    }

    private sealed record DetectionInfoPayload
    {
        public string? ClassName { get; init; }
        public float Score { get; init; }
        public float X1 { get; init; }
        public float Y1 { get; init; }
        public float X2 { get; init; }
        public float Y2 { get; init; }
        public int ObjectId { get; init; }
    }

    private sealed record YoloStatusPacketPayload
    {
        public bool Enabled { get; init; }
        public bool ModelLoaded { get; init; }
        public float ConfThreshold { get; init; }
        public string? LastError { get; init; }
        public string? Source { get; init; }
        public ulong StampNs { get; init; }
        public uint FrameId { get; init; }
    }
}
