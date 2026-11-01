using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NothingShell.App.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;
using Visual = System.Windows.Media.Visual;

namespace NothingShell.App.Rendering;

/// <summary>
/// Vetro e ombra di una superficie (docs/02-design-system.md). WPF con la trasparenza per pixel non sa sfocare ciò che sta
/// dietro e l'acrilico di Windows resta rettangolare, quindi sotto ogni finestra c'è una finestra compagna che non disegna
/// nulla di suo: contiene solo la sfocatura di sistema (composizione di Windows, con angoli antialias del raggio voluto) e
/// porta l'ombra nativa di Windows 11. La finestra WPF le sta sopra con la tinta, il riflesso e il contenuto; i clic restano
/// suo, perché la compagna è grande esattamente quanto la superficie.
/// In risposta ai messaggi di posizione non si chiama mai SetWindowPos per l'ordine: Windows riordina insieme le finestre dello
/// stesso proprietario e rimanda i messaggi alle altre, quindi due widget si rubavano il fondo all'infinito. L'ordine si
/// decide riscrivendo WM_WINDOWPOSCHANGING, che non genera altri messaggi.
/// </summary>
internal sealed class GlassBackdrop
{
    private const uint WsPopup = 0x80000000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const nint HtTransparent = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int AttributeCornerPreference = 33;
    private const int AttributeBorderColor = 34;
    private const int CornerSquare = 1;
    private const int CornerRound = 2;
    private const uint ColorNone = 0xFFFFFFFE;
    private const int AccentHostBackdrop = 5;
    private const int WcaAccentPolicy = 19;
    private const int WmWindowPosChanged = 0x0047;
    private const uint WmWindowPosChanging = 0x0046;
    public const string ClassName = "NothingShellGlass";

    private static readonly List<GlassBackdrop> Alive = [];
    private static readonly HashSet<nint> AtBottom = [];
    private static readonly WndProc Procedure = Proc;
    private static Compositor? _compositor;
    private static bool _unavailable;
    private static bool _classRegistered;
    private static object? _dispatcherQueue;

    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly bool _bottom;
    private nint _hwnd;
    private nint _windowHwnd;
    private object? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _sprite;
    private CompositionRoundedRectangleGeometry? _clip;
    private double _radius;
    private (int X, int Y, int Width, int Height) _placed;
    private (float Width, float Height, float Radius) _shape;
    private Rect? _manual;

    private GlassBackdrop(Window window, FrameworkElement surface, double radius, bool bottom)
    {
        _window = window;
        _surface = surface;
        _radius = radius;
        _bottom = bottom;
    }

    /// <summary>
    /// Mette vetro e ombra sotto <paramref name="surface"/>. La compagna è la proprietaria della finestra, così Windows le tiene
    /// incollate nell'ordine. Con <paramref name="bottom"/> la finestra vive in fondo al desktop (widget): ci sta la compagna,
    /// che si prende il proprietario della finestra.
    /// </summary>
    public static GlassBackdrop Attach(Window window, FrameworkElement surface, double radius = Theme.RadiusLarge, bool bottom = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(surface);
        var backdrop = new GlassBackdrop(window, surface, radius, bottom);
        window.SourceInitialized += (_, _) => backdrop.Create();
        window.IsVisibleChanged += (_, _) => backdrop.Sync();
        window.Closed += (_, _) => backdrop.Destroy();
        surface.SizeChanged += (_, _) => backdrop.Sync();
        Theme.Changed += backdrop.OnThemeChanged;
        return backdrop;
    }

    /// <summary>Forma animata dal chiamante (il notch): rettangolo in DIP dentro la finestra e raggio, a ogni fotogramma.</summary>
    public void SetShape(Rect bounds, double radius)
    {
        _manual = bounds;
        _radius = radius;
        Sync();
    }

    /// <summary>Raggio cambiato (finestra ingrandita: angoli vivi).</summary>
    public void SetRadius(double radius)
    {
        _radius = radius;
        ApplyCorners();
        Sync();
    }

    /// <summary>Allinea la compagna alla superficie: posizione, misura, angoli; nascosta se la finestra non si vede.</summary>
    private void Sync()
    {
        if (_hwnd == 0)
            return;
        if (!_window.IsVisible || _surface.ActualWidth <= 0)
        {
            if (_placed != default)
            {
                SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpHideWindow | SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoZOrder);
                _placed = default;
            }
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(_surface);
        var offset = _manual is { } manual ? new Vector(manual.X, manual.Y) : LayoutOffset();
        WindowNative.GetWindowRect(_windowHwnd, out var rect);
        var (width, height) = _manual is { } m ? (m.Width, m.Height) : (_surface.ActualWidth, _surface.ActualHeight);
        var bounds = (
            X: rect.Left + (int)Math.Round(offset.X * dpi.DpiScaleX),
            Y: rect.Top + (int)Math.Round(offset.Y * dpi.DpiScaleY),
            Width: (int)Math.Round(width * dpi.DpiScaleX),
            Height: (int)Math.Round(height * dpi.DpiScaleY));

        if (_placed == default)
            FadeIn();
        if (bounds != _placed)
        {
            SetWindowPos(_hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow | SwpNoZOrder);
            _placed = bounds;
        }

        var shape = ((float)bounds.Width, (float)bounds.Height, (float)(_radius * dpi.DpiScaleX));
        if (shape != _shape && _root is not null && _sprite is not null && _clip is not null)
        {
            _shape = shape;
            _root.Size = new(shape.Item1, shape.Item2);
            _sprite.Size = new(shape.Item1, shape.Item2);
            _clip.Size = new(shape.Item1, shape.Item2);
            _clip.CornerRadius = new(shape.Item3, shape.Item3);
        }
    }

    /// <summary>Dove sta la superficie nella finestra, senza le trasformazioni delle animazioni d'apertura.</summary>
    private Vector LayoutOffset()
    {
        var total = default(Vector);
        for (DependencyObject? node = _surface; node is not null and not Window; node = VisualTreeHelper.GetParent(node))
            if (node is Visual visual)
                total += VisualTreeHelper.GetOffset(visual);
        return total;
    }

    private void Create()
    {
        _windowHwnd = new WindowInteropHelper(_window).Handle;
        RegisterClass();
        var exStyle = WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap | (_window.Topmost ? WsExTopmost : 0);
        _hwnd = CreateWindowExW(exStyle, ClassName, null, WsPopup | WsThickFrame, 0, 0, 1, 1, 0, 0, 0, 0);
        if (_hwnd == 0)
            return;

        // L'ombra di Windows 11 viene col telaio esteso; il suo bordo chiaro no, c'è già quello della superficie.
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        _ = DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        ApplyCorners();
        var border = unchecked((int)ColorNone);
        _ = DwmSetWindowAttribute(_hwnd, AttributeBorderColor, ref border, sizeof(int));

        if (_bottom)
        {
            AtBottom.Add(_hwnd);
            NativeMethods.SetOwner(_hwnd, GetWindow(_windowHwnd, GwOwner));
        }
        NativeMethods.SetOwner(_windowHwnd, _hwnd);
        ApplyMaterial();
        Alive.Add(this);
        HwndSource.FromHwnd(_windowHwnd)?.AddHook(OnWindowMessage);
        Sync();
    }

    private void ApplyCorners()
    {
        if (_hwnd == 0)
            return;
        var corner = _radius > 0 ? CornerRound : CornerSquare;
        _ = DwmSetWindowAttribute(_hwnd, AttributeCornerPreference, ref corner, sizeof(int));
    }

    private void CreateVisuals()
    {
        if (Compositor() is not { } compositor)
            return;
        try
        {
            EnableHostBackdrop(_hwnd);
            compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(_hwnd, false, out var raw);
            var target = MarshalInspectable<DesktopWindowTarget>.FromAbi(raw);
            _target = target;
            _root = compositor.CreateContainerVisual();
            target.Root = _root;
            _clip = compositor.CreateRoundedRectangleGeometry();
            _sprite = compositor.CreateSpriteVisual();
            _sprite.Clip = compositor.CreateGeometricClip(_clip);
            _root.Children.InsertAtTop(_sprite);
            _sprite.Brush = compositor.CreateHostBackdropBrush();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // niente sfocatura su questa versione di Windows: resta la superficie col suo fondo e l'ombra
            _sprite = null;
        }
    }

    /// <summary>La composizione si carica solo la prima volta che serve il vetro: spento non costa memoria, resta l'ombra.</summary>
    private void ApplyMaterial()
    {
        if (_hwnd == 0)
            return;
        if (Theme.Current.IsGlass && _sprite is null)
        {
            CreateVisuals();
            _shape = default;
        }
        if (_sprite is not null)
            _sprite.IsVisible = Theme.Current.IsGlass;
        Sync();
    }

    /// <summary>Le finestre si aprono con un'animazione: il vetro sfuma insieme a loro invece di comparire prima.</summary>
    private void FadeIn()
    {
        if (_sprite is null || _compositor is not { } compositor || !Motion.Enabled)
            return;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        _sprite.StartAnimation("Opacity", fade);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyMaterial();

    /// <summary>La compagna segue la finestra: spostamenti, trascinamenti, ridimensionamenti.</summary>
    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanged)
            Sync();
        return 0;
    }

    private void Destroy()
    {
        Theme.Changed -= OnThemeChanged;
        Alive.Remove(this);
        AtBottom.Remove(_hwnd);
        if (_hwnd != 0)
            DestroyWindow(_hwnd);
        _hwnd = 0;
        _sprite = null;
        _clip = null;
        _root = null;
        _target = null;
    }

    private static Compositor? Compositor()
    {
        if (_compositor is not null || _unavailable)
            return _compositor;
        try
        {
            // La composizione vuole una coda di messaggi sul thread dell'interfaccia: quella di WPF la fa girare.
            _ = CreateDispatcherQueueController(new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 0 }, out var controller);
            _dispatcherQueue = controller;
            _compositor = new Compositor();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            _unavailable = true;
        }
        return _compositor;
    }

    private static void RegisterClass()
    {
        if (_classRegistered)
            return;
        var info = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure, ClassName = ClassName };
        _ = RegisterClassExW(ref info);
        _classRegistered = true;
    }

    /// <summary>Tutta area client (niente telaio) e mai bersaglio del mouse: per Windows conta solo per l'ombra.</summary>
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmNcCalcSize when wParam != 0:
                return 0;
            case WmNcHitTest:
                return HtTransparent;
            case WmWindowPosChanging when AtBottom.Contains(hwnd):
                var pos = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
                if ((pos.Flags & NativeMethods.SwpNoZOrder) == 0 && pos.HwndInsertAfter != NativeMethods.HwndBottom)
                {
                    pos.HwndInsertAfter = NativeMethods.HwndBottom;
                    Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
                }
                break;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnableHostBackdrop(nint hwnd)
    {
        var accent = new AccentPolicy { State = AccentHostBackdrop };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new CompositionAttribute { Attribute = WcaAccentPolicy, Data = pointer, Size = size };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const uint GwOwner = 4;

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WndProc Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public int Color;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttribute
    {
        public int Attribute;
        public nint Data;
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref CompositionAttribute data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);
}
