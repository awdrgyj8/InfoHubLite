using System.Runtime.InteropServices;

namespace InfoHubLite;

/// <summary>
/// Tray icon, popup menu and power notifications on raw Win32 — a hidden window and a message loop, so the
/// resident process never loads a UI framework.
/// </summary>
sealed class Tray
{
    public const int CmdScreen = 1, CmdSettings = 2, CmdAutostart = 3, CmdQuit = 4, CmdAdmin = 5;
    const uint WM_DESTROY = 0x02, WM_ENDSESSION = 0x16, WM_POWERBROADCAST = 0x218, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_TRAY = 0x8001;

    readonly Func<(bool screenOn, bool autostart)> _state;
    readonly Action<int> _command;
    readonly Action<bool> _power; // true = suspending, false = resumed
    readonly WndProc _proc;       // field keeps the delegate alive for as long as the window exists
    uint _taskbarCreated;
    NOTIFYICONDATA _nid;

    public Tray(Func<(bool, bool)> state, Action<int> command, Action<bool> power)
    {
        _state = state; _command = command; _power = power; _proc = Proc;
    }

    public void Run()
    {
        nint inst = GetModuleHandleW(null);
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = inst, lpszClassName = "InfoHubLiteTray" };
        RegisterClassExW(ref wc);
        // A real (never shown) top-level window: message-only windows do not receive WM_POWERBROADCAST.
        nint hwnd = CreateWindowExW(0, wc.lpszClassName, "InfoHubLite", 0, 0, 0, 0, 0, 0, 0, inst, 0);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, uFlags = 1 | 2 | 4 /*MESSAGE|ICON|TIP*/,
            uCallbackMessage = (int)WM_TRAY, hIcon = AppIcon(inst), szTip = "InfoHub Lite",
        };
        Shell_NotifyIconW(0 /*NIM_ADD*/, ref _nid);
        while (GetMessageW(out MSG m, 0, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
        Shell_NotifyIconW(2 /*NIM_DELETE*/, ref _nid);
    }

    nint Proc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case WM_TRAY when (uint)l is WM_RBUTTONUP or WM_LBUTTONUP:
                var (screenOn, autostart) = _state();
                nint menu = CreatePopupMenu();
                AppendMenuW(menu, screenOn ? 8u : 0, CmdScreen, "螢幕開啟");
                AppendMenuW(menu, 0, CmdSettings, "設定…");
                AppendMenuW(menu, autostart ? 8u : 0, CmdAutostart, "開機自動啟動");
                // Temperature, voltage and fan sensors need the hardware driver, which needs elevation.
                // ponytail: the Run-key autostart launches unelevated; a scheduled task is the upgrade if this is used daily.
                if (!Environment.IsPrivilegedProcess) AppendMenuW(menu, 0, CmdAdmin, "以系統管理員重新啟動（溫度／風扇／電壓）");
                AppendMenuW(menu, 0x800, 0, null);
                AppendMenuW(menu, 0, CmdQuit, "結束");
                GetCursorPos(out POINT p);
                SetForegroundWindow(hwnd); // otherwise the menu will not dismiss when clicking elsewhere
                int cmd = TrackPopupMenu(menu, 0x100 | 2 /*RETURNCMD|RIGHTBUTTON*/, p.x, p.y, 0, hwnd, 0);
                DestroyMenu(menu);
                if (cmd == CmdQuit) DestroyWindow(hwnd); else if (cmd != 0) _command(cmd);
                return 0;
            case WM_POWERBROADCAST:
                if ((int)w == 4 /*PBT_APMSUSPEND*/) _power(true);
                else if ((int)w is 0x12 or 7 /*RESUMEAUTOMATIC | RESUMESUSPEND*/) _power(false);
                return 1;
            case WM_ENDSESSION when w != 0:
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        if (msg == _taskbarCreated && msg != 0) Shell_NotifyIconW(0, ref _nid); // Explorer restarted: put the icon back
        return DefWindowProcW(hwnd, msg, w, l);
    }

    delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public nint hWnd; public int uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    const string U = "user32";
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport(U, CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport(U, CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport(U)] static extern bool DestroyWindow(nint hwnd);
    [DllImport(U)] static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport(U)] static extern int GetMessageW(out MSG m, nint hwnd, uint min, uint max);
    [DllImport(U)] static extern bool TranslateMessage(ref MSG m);
    [DllImport(U)] static extern nint DispatchMessageW(ref MSG m);
    [DllImport(U)] static extern void PostQuitMessage(int code);
    [DllImport(U, CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport(U)] static extern nint LoadIconW(nint inst, nint id);
    [DllImport(U)] static extern nint LoadImageW(nint inst, nint id, uint type, int cx, int cy, uint flags);
    [DllImport(U)] static extern int GetSystemMetrics(int index);

    /// <summary>The logo embedded in this exe (ApplicationIcon lands at resource id 32512), at the tray's size.</summary>
    static nint AppIcon(nint inst)
    {
        nint icon = LoadImageW(inst, 32512, 1 /*IMAGE_ICON*/, GetSystemMetrics(49 /*SM_CXSMICON*/), GetSystemMetrics(50 /*SM_CYSMICON*/), 0);
        return icon != 0 ? icon : LoadIconW(0, 32512 /*IDI_APPLICATION: generic fallback*/);
    }
    [DllImport(U)] static extern nint CreatePopupMenu();
    [DllImport(U, CharSet = CharSet.Unicode)] static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport(U)] static extern bool DestroyMenu(nint menu);
    [DllImport(U)] static extern bool GetCursorPos(out POINT p);
    [DllImport(U)] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport(U)] static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(int msg, ref NOTIFYICONDATA d);
}
