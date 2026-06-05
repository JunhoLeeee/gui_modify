namespace BroadcastControl.App.Services;

// 시스템 로그 서비스의 계약입니다.
// 각 기능에서 발생한 문자열 로그를 MonitoringView에 추가하기 위해 사용합니다.
public interface ISystemLogService
{
    event EventHandler<string>? LogAdded;

    void Add(string message);
}
