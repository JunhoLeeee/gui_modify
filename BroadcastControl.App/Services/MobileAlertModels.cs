// 모바일 브라우저 알림 페이지로 전달할 위험 이벤트 데이터 모델 파일이다.
// MobileAlertHubService가 이 모델을 JSON으로 직렬화해 SSE 이벤트와 /latest API 응답에 사용한다.
namespace BroadcastControl.App.Services;

public sealed record MobileAlertEvent(
    string Id,
    string CreatedAt,
    string Title,
    string VlmAnalysis,
    string DetectionSummary,
    string ThreatLevel,
    string EvidenceUrl);
