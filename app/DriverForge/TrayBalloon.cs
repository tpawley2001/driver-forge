using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DriverForge;

/// <summary>
/// A notification-area icon with one balloon (Shell_NotifyIconW), which
/// Windows 10/11 shows as a notification. Returns whether it was clicked.
/// </summary>
static class TrayBalloon
{
    const int WM_APP_TRAY = 0x8001, NIN_BALLOONUSERCLICK = 0x0405;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIIF_INFO = 0x1;

    public static bool Show(string title, string text, TimeSpan wait)
    {
        var window = new HwndSource(new HwndSourceParameters("DriverForgeTray") { ParentWindow = new IntPtr(-3) });   // message-only
        bool clicked = false;
        var frame = new DispatcherFrame();
        window.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WM_APP_TRAY && (lParam & 0xFFFF) == NIN_BALLOONUSERCLICK) { clicked = true; frame.Continue = false; }
            return 0;
        });
        ExtractIconExW(Environment.ProcessPath!, 0, out nint large, out nint icon, 1);
        if (large != 0) DestroyIcon(large);
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = icon,
            szTip = title,
            szInfo = text,
            szInfoTitle = title,
            dwInfoFlags = NIIF_INFO,
            uVersion = NOTIFYICON_VERSION_4,
        };
        try
        {
            if (!Shell_NotifyIconW(NIM_ADD, ref data)) return false;
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
            var timer = new DispatcherTimer { Interval = wait };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            return clicked;
        }
        finally
        {
            Shell_NotifyIconW(NIM_DELETE, ref data);
            if (icon != 0) DestroyIcon(icon);
            window.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;   // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconExW(string file, int index, out nint large, out nint small, uint icons);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint icon);
}
