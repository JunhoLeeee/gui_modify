using System.Windows;
using BroadcastControl.App.Models.Camera;

namespace BroadcastControl.App.Services;

// YOLO 바운딩 박스를 카메라 화면 위에 정확히 올리기 위한 좌표 보정 유틸리티입니다.
// 원본 영상 비율과 WPF Viewport 크기를 비교해 실제 표시 영역과 프레임 내부 여부를 계산합니다.
public static class DetectionOverlayService
{
    public static Rect GetUniformContentRect(int sourceWidth, int sourceHeight, double viewportWidth, double viewportHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(viewportWidth / sourceWidth, viewportHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Rect((viewportWidth - width) / 2.0, (viewportHeight - height) / 2.0, width, height);
    }

    public static bool IsInsideFrame(DetectionInfo detection, int frameWidth, int frameHeight)
    {
        return detection.X2 > 0 &&
               detection.Y2 > 0 &&
               detection.X1 < frameWidth &&
               detection.Y1 < frameHeight;
    }
}
