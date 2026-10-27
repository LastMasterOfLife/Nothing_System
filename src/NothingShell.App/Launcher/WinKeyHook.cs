using System.Runtime.InteropServices;
using NothingShell.Core.Launcher;

namespace NothingShell.App.Launcher;

/// <summary>
/// Hook di tastiera di basso livello su un thread dedicato con il proprio ciclo di messaggi:
/// Windows rimuove gli hook che rispondono lentamente, quindi non deve mai dipendere dal thread UI.
/// Solleva <see cref="Pressed"/> (sul thread dell'hook) quando Win viene premuto da solo.
/// </summary>
public sealed class WinKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    private readonly WinKeyGesture _gesture = new();
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private Thread? _thread;
    private uint _threadId;

    public WinKeyHook()
    {
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    public event Action? Pressed;

    public bool Enabled
    {
        get => _gesture.Enabled;
        set => _gesture.Enabled = value;
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            var keyboard = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            var mouse = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            ready.Set();
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (keyboard != 0)
                UnhookWindowsHookEx(keyboard);
            if (mouse != 0)
                UnhookWindowsHookEx(mouse);
        })
        {
            IsBackground = true,
            Name = "WinKeyHook",
        };
        _thread.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _threadId = 0;
    }

    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KeyboardData>(lParam);
            var down = wParam is WmKeyDown or WmSysKeyDown;
            var injected = (data.Flags & LlkhfInjected) != 0;
            if (_gesture.OnKey((int)data.VirtualKey, down, injected) == KeyDecision.OpenLauncher)
            {
                // Il rilascio originale viene bloccato; Windows vede Win + maschera + rilascio:
                // è una combinazione, quindi il suo Start non si apre.
                keybd_event(WinKeyGesture.MaskKey, 0, 0, 0);
                keybd_event(WinKeyGesture.MaskKey, 0, KeyEventKeyUp, 0);
                keybd_event((byte)data.VirtualKey, 0, KeyEventExtendedKey | KeyEventKeyUp, 0);
                Pressed?.Invoke();
                return 1;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam is WmLButtonDown or WmRButtonDown)
            _gesture.OnMouseButton();
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Hwnd;
        public uint Msg;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);
}
