namespace BroadcastControl.App.Services;

// 시스템 로그 한 줄을 ViewModel 컬렉션으로 전달하는 단순 이벤트 서비스입니다.
// UDP 수신 오류, 녹화 상태, 모터 명령 결과 같은 메시지를 한 통로로 모읍니다.
public sealed class SystemLogService : ISystemLogService
{
    public event EventHandler<string>? LogAdded;

    public void Add(string message)
    {
        LogAdded?.Invoke(this, message);
    }
}
