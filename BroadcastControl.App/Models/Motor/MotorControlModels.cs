namespace BroadcastControl.App.Models.Motor;

// GUI 방향키 입력을 Jetson gui_bridge의 btn_mask 1바이트 값으로 변환하기 위한 플래그입니다.
// 여러 방향이 동시에 눌릴 수 있으므로 [Flags]로 조합합니다.
[Flags]
public enum MotorButtonMask : byte
{
    // 방향키가 눌리지 않은 상태입니다.
    None = 0,
    // Pan을 오른쪽으로 이동시키는 명령입니다.
    Right = 0x02,
    // Pan을 왼쪽으로 이동시키는 명령입니다.
    Left = 0x01,
    // Tilt를 위로 이동시키는 명령입니다.
    Up = 0x08,
    // Tilt를 아래로 이동시키는 명령입니다.
    Down = 0x04,
    // 모터 중심 위치 이동이 필요할 때 사용하는 예비 명령입니다.
    Center = 0x10
}
