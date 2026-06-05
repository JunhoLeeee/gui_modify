using System.ComponentModel;
using System.Runtime.CompilerServices;

// 파일 역할:
// 모든 ViewModel이 공통으로 사용하는 UI 갱신 알림 기반 클래스입니다.
// 속성 값이 바뀔 때 WPF Binding에 PropertyChanged 이벤트를 알려 화면을 자동 갱신합니다.

namespace BroadcastControl.App.ViewModels;

/// <summary>
/// 기능별 ViewModel의 공통 기반 클래스입니다.
/// SetProperty를 사용하면 값 변경 여부 확인과 PropertyChanged 알림을 한 번에 처리할 수 있습니다.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
