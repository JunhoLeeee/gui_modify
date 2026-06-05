using System.Buffers.Binary;
using BroadcastControl.App.Models.Motor;

namespace BroadcastControl.App.Services;

// MotorControlViewModel의 모드, tracking, track_id, 방향키, Pan/Tilt raw값, 속도 값을
// Jetson gui_bridge가 해석하는 고정 길이 UDP 커맨드 패킷으로 직렬화합니다.
// 패킷 형태는 JunhoLeeee/gui_modify의 11바이트 GUI -> Jetson 모터 패킷 규격에 맞춥니다.
public static class MotorPacketSerializer
{
    public const int CommandPacketSize = 11;

    public static byte[] CreateCommandPacket(
        byte mode,
        byte tracking,
        byte trackId,
        bool isEoPrimary,
        MotorButtonMask btnMask,
        ushort panPos,
        ushort tiltPos,
        byte scanStep,
        byte manualStep)
    {
        var packet = new byte[CommandPacketSize];
        packet[0] = mode;
        packet[1] = tracking;
        packet[2] = trackId;
        packet[3] = isEoPrimary ? (byte)0 : (byte)1;
        packet[4] = EncodeButtonMask(btnMask);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), panPos);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7, 2), tiltPos);
        packet[9] = EncodeStepSize(scanStep);
        packet[10] = EncodeStepSize(manualStep);
        return packet;
    }

    private static byte EncodeStepSize(int stepSize)
    {
        return (byte)Math.Clamp(stepSize, 1, 10);
    }

    private static byte EncodeButtonMask(MotorButtonMask buttons)
    {
        return (byte)((byte)buttons & 0x1F);
    }
}
