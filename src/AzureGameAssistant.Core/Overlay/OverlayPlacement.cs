namespace AzureGameAssistant.Core.Overlay;

/// <summary>
/// Pure corner math for the desktop overlay. The caller supplies the target
/// monitor work area in DIPs so multi-monitor placement stays testable without
/// a window; offsets are honoured until the overlay would leave the monitor.
/// </summary>
public static class OverlayPlacement
{
    public static (double Left, double Top) Place(
        string? corner,
        double areaLeft,
        double areaTop,
        double areaWidth,
        double areaHeight,
        double width,
        double height,
        double offsetX,
        double offsetY)
    {
        bool right = corner is "TopRight" or "BottomRight";
        bool bottom = corner is "BottomLeft" or "BottomRight";
        double left = right ? areaLeft + areaWidth - width - offsetX : areaLeft + offsetX;
        double top = bottom ? areaTop + areaHeight - height - offsetY : areaTop + offsetY;

        return (
            ClampStart(left, areaLeft, areaWidth, width),
            ClampStart(top, areaTop, areaHeight, height));
    }

    private static double ClampStart(double start, double areaStart, double areaSpan, double size)
    {
        double max = Math.Max(areaStart, areaStart + areaSpan - size);
        return Math.Clamp(start, areaStart, max);
    }
}
