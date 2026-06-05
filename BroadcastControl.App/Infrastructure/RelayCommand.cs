using System.Windows.Input;

namespace BroadcastControl.App.Infrastructure;

// 파일 역할:
// 버튼 클릭, 키 입력, 메뉴 선택 같은 View 이벤트를 ViewModel 함수로 연결하는 공통 ICommand 구현입니다.
// XAML Command 바인딩에서 실행 함수와 실행 가능 조건을 함께 전달할 때 사용합니다.
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
