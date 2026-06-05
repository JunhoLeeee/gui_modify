using System.Windows.Media.Imaging;

namespace BroadcastControl.App.Models.Camera;

// Jetson에서 받은 JPEG 영상 패킷을 한 프레임으로 조립한 결과입니다.
// CameraView의 EO/IR 영상 표시와 탐지 박스 좌표 매칭에 사용됩니다.
public readonly record struct ReceivedVideoFrame(
    ulong StampNs,
    uint FrameIndex,
    ushort Width,
    ushort Height,
    BitmapSource Bitmap);

// YOLO/추적 패킷에서 객체 하나를 파싱한 결과입니다.
// 바운딩 박스 그리기, YOLO Targets 리스트, 클릭한 객체의 track_id 선택에 사용됩니다.
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
    public string LabelText => $"{ClassName} object{ObjectId} ({Score:0.00})";
}

// 탐지 결과가 어떤 영상 스트림에서 왔는지 구분합니다.
// EO/IR이 같은 탐지 포트로 들어와도 큰 화면 기준 객체 선택을 할 수 있게 해줍니다.
public enum DetectionStream
{
    Unknown = 0,
    Eo = 1,
    Ir = 2
}

// 한 프레임에 포함된 YOLO/추적 객체 목록입니다.
// CameraView의 바운딩 박스 렌더링과 MonitoringViewModel의 위험 객체 우선순위 계산에 사용됩니다.
public readonly record struct DetectionPacket(
    ulong StampNs,
    uint FrameId,
    int Width,
    int Height,
    IReadOnlyList<DetectionInfo> Detections,
    DetectionStream Stream = DetectionStream.Unknown,
    int ActiveTrackId = 0xFF);

// Jetson 쪽 YOLO 처리 상태를 나타내는 패킷입니다.
// 모델 로드 여부, confidence 기준, 마지막 오류를 GUI 상태 로그에 표시할 때 사용합니다.
public readonly record struct YoloStatusPacket(
    bool Enabled,
    bool ModelLoaded,
    float ConfThreshold,
    string LastError,
    string Source,
    ulong StampNs,
    uint FrameId);

// VLM 추론 결과의 행동 분류입니다.
public enum VlmBehavior { Unknown = 0, Standing = 1, Approaching = 2, Shooting = 3, Aiming = 4 }

// VLM 추론 결과의 무기 분류입니다.
public enum VlmWeapon { Unknown = 0, None = 1, Gun = 2, Knife = 3 }

// VLM 추론 결과의 직업/역할 분류입니다.
public enum VlmJob { Unknown = 0, Civilian = 1, Soldier = 2 }

// VLM 추론 결과 한 개체입니다. track_id 기준으로 기존 탐지 패킷의 바운딩박스와 매핑됩니다.
public readonly record struct VlmResult(int TrackId, VlmBehavior Behavior, VlmWeapon Weapon, VlmJob Job)
{
    public bool HasAnyData =>
        Behavior != VlmBehavior.Unknown ||
        Weapon != VlmWeapon.Unknown ||
        Job != VlmJob.Unknown;
}

// MEVA 같은 재생 영상의 현재 구간 정보를 나타냅니다.
// 영상 구간이 바뀌거나 반복 재생될 때 시스템 로그 메시지를 만들기 위해 사용합니다.
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
