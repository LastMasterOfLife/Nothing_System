namespace NothingShell.Core.Design;

/// <summary>Conversione in scala di grigi delle icone (stile icone monocromatiche Nothing).</summary>
public static class Monochrome
{
    /// <summary>Luminanza minima: le icone scure restano leggibili sul nero.</summary>
    public const byte Floor = 70;

    /// <summary>
    /// Pixel BGRA premoltiplicati (bitmap della shell) → non premoltiplicati, in place.
    /// Se tutta l'alfa è 0 la bitmap non ha canale alfa: diventa opaca.
    /// </summary>
    public static void Unpremultiply(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (!hasAlpha)
            {
                pixels[i + 3] = 255;
                continue;
            }
            var a = pixels[i + 3];
            if (a is 0 or 255)
                continue;
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }
    }

    /// <summary>Converte in place pixel BGRA (non premoltiplicati), alfa invariato.</summary>
    public static void ApplyBgra(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var luminance = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            var value = (byte)Math.Round(Floor + luminance * (255 - Floor) / 255);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
        }
    }
}
