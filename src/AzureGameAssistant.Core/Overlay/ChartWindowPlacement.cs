namespace AzureGameAssistant.Core.Overlay;

/// <summary>
/// Pure restore math for detached chart windows. The caller supplies the virtual
/// desktop bounds in DIPs so saved geometry can be validated without a window.
/// </summary>
public static class ChartWindowPlacement
{
    public const double MinWidth = 360;
    public const double MinHeight = 200;

    public static bool TryRestore(
        double? left,
        double? top,
        double? width,
        double? height,
        double screenLeft,
        double screenTop,
        double screenWidth,
        double screenHeight,
        out (double Left, double Top, double Width, double Height) bounds)
    {
        bounds = default;
        if (left is not double x ||
            top is not double y ||
            width is not double w ||
            height is not double h ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(w) ||
            !double.IsFinite(h) ||
            screenWidth <= 0 ||
            screenHeight <= 0)
        {
            return false;
        }

        w = Math.Clamp(w, MinWidth, Math.Max(MinWidth, screenWidth));
        h = Math.Clamp(h, MinHeight, Math.Max(MinHeight, screenHeight));

        const double minVisibleWidth = 80;
        const double minVisibleHeight = 60;
        double visibleWidth = Math.Min(x + w, screenLeft + screenWidth) - Math.Max(x, screenLeft);
        double visibleHeight = Math.Min(y + h, screenTop + screenHeight) - Math.Max(y, screenTop);
        if (visibleWidth < minVisibleWidth || visibleHeight < minVisibleHeight)
        {
            return false;
        }

        double screenRight = screenLeft + screenWidth;
        double screenBottom = screenTop + screenHeight;
        bounds = (
            Math.Clamp(x, screenLeft, screenRight - w),
            Math.Clamp(y, screenTop, screenBottom - h),
            w,
            h);
        return true;
    }
}
