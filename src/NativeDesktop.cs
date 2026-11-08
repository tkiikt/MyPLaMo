using System.Runtime.InteropServices;

namespace MyPLaMo;

// WinUI owns the UI and message loop; only tray, global shortcut and input use Win32.
sealed class NativeDesktop : IDisposable
{
    const uint TrayMessage = 0x8001;
    readonly nint hwnd;
    readonly Action reveal, quit, capture;
    readonly SubclassProc procedure;
    readonly uint taskbarCreated;
    NotifyIconData icon;

    public NativeDesktop(nint hwnd, Action reveal, Action quit, Action capture)
    {
        this.hwnd = hwnd; this.reveal = reveal; this.quit = quit; this.capture = capture;
        procedure = WindowProc;
        if (!SetWindowSubclass(hwnd, procedure, 1, 0)) throw new IOException("ウィンドウの初期化に失敗しました。");
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        icon = new NotifyIconData {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1, Flags = 7,
            Callback = TrayMessage, Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "MyPLaMo.ico"), 1, 0, 0, 0x10),
            Tip = "MyPLaMo", Info = "", InfoTitle = ""
        };
        Shell_NotifyIcon(0, ref icon);
    }

    public bool RegisterShortcut(string key)
    {
        UnregisterHotKey(hwnd, 44);
        return key.Length != 1 || RegisterHotKey(hwnd, 44, 0x4003, char.ToUpperInvariant(key[0]));
    }

    nint WindowProc(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (message == taskbarCreated) Shell_NotifyIcon(0, ref icon);
        if (message == 0x312 && wparam == 44) capture();
        if (message == TrayMessage) {
            if ((int)lparam == 0x203 || (int)lparam == 0x202) reveal();
            if ((int)lparam == 0x205) {
                var menu = CreatePopupMenu();
                try {
                    AppendMenu(menu, 0, 1, "翻訳画面を開く");
                    AppendMenu(menu, 0, 2, "終了");
                    GetCursorPos(out var point); SetForegroundWindow(hwnd);
                    var command = TrackPopupMenu(menu, 0x182, point.X, point.Y, 0, hwnd, 0);
                    if (command == 1) reveal();
                    if (command == 2) quit();
                    PostMessage(hwnd, 0, 0, 0);
                } finally { DestroyMenu(menu); }
            }
        }
        if (message == 0x24) {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lparam);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            limits.MinTrack = new Point { X = (int)(860 * scale), Y = (int)(620 * scale) };
            Marshal.StructureToPtr(limits, lparam, false);
        }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    public void Dispose()
    {
        UnregisterHotKey(hwnd, 44);
        Shell_NotifyIcon(2, ref icon);
        RemoveWindowSubclass(hwnd, procedure, 1);
        if (icon.Icon != 0) DestroyIcon(icon.Icon);
    }

    public static void ShowAlreadyRunning() => MessageBox(0, "MyPLaMo は起動中です。通知領域のアイコンから開けます。", "MyPLaMo", 0);
    public static bool ModifierDown => GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0;
    public static void SendControlKey(ushort key)
    {
        Input Key(ushort code, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = code, Flags = up ? 2u : 0u } } };
        Input[] keys = [Key(0x11, false), Key(key, false), Key(key, true), Key(0x11, true)];
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length) throw new IOException("元のアプリにキーを送れませんでした。");
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct NotifyIconData {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32")] static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32")] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32")] static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32")] static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32")] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32")] static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32")] static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] static extern bool PostMessage(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int MessageBox(nint hwnd, string text, string title, uint flags);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
}
