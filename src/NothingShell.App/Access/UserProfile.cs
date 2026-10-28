using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.System;

namespace NothingShell.App.Access;

/// <summary>Nome e immagine dell'account Windows per la card di accesso (docs/09-access.md).</summary>
public sealed record UserProfile(string DisplayName, string UserName, BitmapSource? Picture)
{
    public static UserProfile Fallback { get; } = new(Environment.UserName, Environment.UserName.ToLowerInvariant(), null);

    /// <summary>API WinRT <c>Windows.System.User</c>; ripiego su GetUserNameEx e sul nome utente.</summary>
    public static async Task<UserProfile> LoadAsync()
    {
        var userName = Environment.UserName.ToLowerInvariant();
        string? name = null;
        BitmapSource? picture = null;
        try
        {
            var users = await User.FindAllAsync(UserType.LocalUser, UserAuthenticationStatus.LocallyAuthenticated);
            if (users.Count > 0)
            {
                var user = users[0];
                name = await user.GetPropertyAsync(KnownUserProperties.DisplayName) as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    var first = await user.GetPropertyAsync(KnownUserProperties.FirstName) as string;
                    var last = await user.GetPropertyAsync(KnownUserProperties.LastName) as string;
                    name = $"{first} {last}".Trim();
                }
                picture = await LoadPictureAsync(user);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            // API non disponibile: si usano i ripieghi
        }

        if (string.IsNullOrWhiteSpace(name))
            name = AccessNative.DisplayName();
        if (string.IsNullOrWhiteSpace(name))
            name = Environment.UserName;
        return new UserProfile(name, userName, picture);
    }

    private static async Task<BitmapSource?> LoadPictureAsync(User user)
    {
        var reference = await user.GetPictureAsync(UserPictureSize.Size424x424);
        if (reference is null)
            return null;
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0)
            return null;
        using var managed = stream.AsStreamForRead();
        var memory = new MemoryStream();
        await managed.CopyToAsync(memory);
        memory.Position = 0;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return IsDefaultPicture(bitmap) ? null : bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            return null;
        }
        finally
        {
            await memory.DisposeAsync();
        }
    }

    /// <summary>
    /// Senza foto Windows restituisce la sagoma grigia su fondo chiaro: tutta in scala di grigi e con
    /// gli angoli quasi bianchi. In quel caso la card usa le iniziali a punti.
    /// </summary>
    private static bool IsDefaultPicture(BitmapSource picture)
    {
        var bgra = new FormatConvertedBitmap(picture, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        for (var i = 0; i < pixels.Length; i += 4 * 7) // campione: un pixel ogni 7
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 12)
                return false; // c'è colore: è una foto vera
        }
        static bool Light(byte[] p, int i) => p[i + 3] == 0 || p[i] > 0xE0; // trasparente o quasi bianco
        return Light(pixels, 0) && Light(pixels, (width - 1) * 4);
    }
}
