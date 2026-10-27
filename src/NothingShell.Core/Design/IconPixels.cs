namespace NothingShell.Core.Design;

/// <summary>Correzioni sui pixel delle icone lette dalla shell; i colori originali restano invariati.</summary>
public static class IconPixels
{
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
}
