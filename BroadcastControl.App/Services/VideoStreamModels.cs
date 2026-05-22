// EO/IR 영상 수신 서비스가 MainWindow와 ViewModel에 전달하는 영상/탐지/상태 모델 파일이다.
// UDP에서 파싱된 프레임, 바운딩 박스, YOLO 상태, 녹화 segment 정보를 한곳에 정의한다.
using System.Windows.Media.Imaging;

namespace BroadcastControl.App.Services;

public readonly record struct ReceivedVideoFrame(
    ulong StampNs,
    uint FrameIndex,
    ushort Width,
    ushort Height,
    BitmapSource Bitmap);

// 탐지 객체 하나의 바운딩 박스와 추적 ID다.
// ObjectId는 Jetson의 track_id를 GUI 내부 이름으로 보관한 값이며, 모터 추적 대상 ID로도 사용한다.
public readonly record struct DetectionInfo(
    string ClassName,
    float Score,
    float X1,
    float Y1,
    float X2,
    float Y2,
    int ObjectId,
    string ThreatLevel = "")
{
    // overlay label에 표시할 짧은 문구다.
    public string LabelText => $"{ClassName} object{ObjectId} ({Score:0.00})";
}

// 한 프레임에 대응되는 탐지 결과 묶음이다.
// 영상 frame id와 detection frame id를 맞춰 큰 화면 위에 바운딩 박스를 그릴 때 사용한다.
public enum DetectionStream
{
    Unknown = 0,
    Eo = 1,
    Ir = 2
}

public readonly record struct DetectionPacket(
    ulong StampNs,
    uint FrameId,
    int Width,
    int Height,
    IReadOnlyList<DetectionInfo> Detections,
    DetectionStream Stream = DetectionStream.Unknown);

// YOLO 또는 bridge 쪽 상태 진단 패킷이다.
// 모델 로딩 여부, confidence threshold, 마지막 오류를 시스템 로그에 표시할 수 있게 한다.
public readonly record struct YoloStatusPacket(
    bool Enabled,
    bool ModelLoaded,
    float ConfThreshold,
    string LastError,
    string Source,
    ulong StampNs,
    uint FrameId);

// Jetson 또는 테스트 영상 재생 segment가 바뀌었을 때의 상태다.
// 로그 메시지 생성 메서드는 UI/진단 로그에서 같은 문구를 재사용하기 위한 도우미다.
public readonly record struct PlaybackSegmentInfo(
    uint ClipIndex,
    uint ClipCount,
    uint SegmentStartSeconds,
    uint SegmentEndSeconds,
    uint CurrentPlaybackSeconds,
    uint CycleIndex)
{
    public string ToLogMessage()
    {
        return $"MEVA video segment changed: clip {ClipIndex}/{ClipCount} now playing {FormatTime(SegmentStartSeconds)} ~ {FormatTime(SegmentEndSeconds)}";
    }

    public string ToLoopRestartLogMessage()
    {
        return $"MEVA video segment replay restarted: clip {ClipIndex}/{ClipCount} now replaying {FormatTime(SegmentStartSeconds)} ~ {FormatTime(SegmentEndSeconds)}";
    }

    public string GetSignature()
    {
        return $"{ClipIndex}:{ClipCount}:{SegmentStartSeconds}:{SegmentEndSeconds}";
    }

    private static string FormatTime(uint totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }
}
