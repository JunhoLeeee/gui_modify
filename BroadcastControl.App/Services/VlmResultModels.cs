// 외부 VLM 분석 프로세스가 GUI로 보낸 위험도 결과를 담는 모델 파일이다.
// 전체 위험도와 객체별 위험도를 함께 보관해서 시스템 상태, 바운딩 박스 색상, 모터 추적 ID 선택에 사용한다.
namespace BroadcastControl.App.Services;

public readonly record struct VlmResultPacket(
    string ThreatLevel,
    string AnalysisMessage,
    string DetectionSummary,
    uint? FrameId,
    IReadOnlyDictionary<int, string> ObjectThreatLevels,
    DateTime ReceivedAt);
