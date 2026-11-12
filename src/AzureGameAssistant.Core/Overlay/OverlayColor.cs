using System.Globalization;

namespace AzureGameAssistant.Core.Overlay;

public static class OverlayColor
{
    public static bool TryParse(string? text, out (byte A, byte R, byte G, byte B) color)
    {
        color = default;
        string value = (text ?? string.Empty).Trim().TrimStart('#');
        if (value.Length is not (6 or 8) ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
        {
            return false;
        }

        if (value.Length == 6)
        {
            hex |= 0xFF000000;
        }

        color = ((byte)(hex >> 24), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        return true;
    }

    public static string Normalize(string? text)
    {
        if (!TryParse(text, out (byte a, byte r, byte g, byte b) color))
        {
            return "#FFFFFF";
        }

        return color.a == 0xFF
            ? $"#{color.r:X2}{color.g:X2}{color.b:X2}"
            : $"#{color.a:X2}{color.r:X2}{color.g:X2}{color.b:X2}";
    }
}
