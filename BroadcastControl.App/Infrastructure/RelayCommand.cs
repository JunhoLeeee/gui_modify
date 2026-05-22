// WPF MVVM에서 버튼과 ViewModel 메서드를 연결하기 위한 공통 Command 파일이다.
// XAML Button.Command가 이 객체를 호출하면 ViewModel 안의 실제 동작 메서드가 실행된다.
using System.Windows.Input;

namespace BroadcastControl.App.Infrastructure;

/// <summary>
/// WPF 버튼과 ViewModel 메서드를 연결하는 기본 ICommand 구현체임.
/// 실행 동작과 현재 실행 가능 여부를 함께 관리함.
/// </summary>
public sealed class RelayCommand : ICommand
{
    /// <summary>
    /// 실제 실행할 동작 보관.
    /// 예: 설정창 열기, 녹화 시작, 모드 변경.
    /// </summary>
    private readonly Action<object?> _execute;

    /// <summary>
    /// 현재 명령 실행 가능 여부를 판단하는 조건임.
    /// 값이 없으면 항상 실행 가능으로 처리함.
    /// </summary>
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    /// 버튼 활성/비활성 상태 재계산을 WPF에 알리는 이벤트임.
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 현재 명령 실행 가능 여부 반환.
    /// </summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <summary>
    /// 실제 동작 실행.
    /// </summary>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// 실행 가능 여부 변경을 WPF에 알림.
    /// 버튼 회색/활성 상태를 즉시 갱신할 때 호출함.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
