using System.Runtime.InteropServices;

namespace EdgePanel.Core;

/// <summary>免安装 WinUI3 用到的少量 Win32 互操作。</summary>
internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_DLGFRAME = 0x00400000L;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>屏幕矩形（GetWindowRect 的返回结构）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>把窗口标记为工具窗（不在 Alt+Tab / 任务栏出现）并设置圆角。</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// 彻底剥掉窗口的非客户区边框。
    /// WinUI3 的 SetBorderAndTitleBar(false,false) 仍会留下 WS_DLGFRAME，
    /// 于是窗口四周有一圈 3px 的"客户区外边框"——它就是深色胶囊外面那圈白框
    /// （客户区因此只有 52×230，而窗口是 58×236）。改成纯 WS_POPUP 后
    /// 客户区与窗口矩形重合，胶囊才能真正贴到边缘。
    /// </summary>
    public static void RemoveWindowFrame(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        _ = SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetRoundedCorners(IntPtr hwnd, bool round)
    {
        var pref = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>DWMWA_BORDER_COLOR 的"无色"取值，传入即隐藏 DWM 描边。</summary>
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>
    /// 去掉 DWM 给窗口画的浅色描边。Windows 11 默认会在窗口四周兜一圈白边，
    /// 深色胶囊贴上它就会看到"胶囊周围有白框"。
    /// </summary>
    public static void SetBorderColorNone(IntPtr hwnd)
    {
        var none = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    // ————————————————————————— 窗口裁剪（胶囊圆角） —————————————————————————

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 把窗口裁剪成圆角矩形（收起条 / 胶囊态用）。
    /// 只靠 XAML 的 CornerRadius 不够：没启用 SystemBackdrop 时窗口底色是不透明的
    /// 主题纯色，圆角以外的像素会露出方角；SetWindowRgn 直接把窗口区域裁掉，圆角才是真的圆。
    /// 半径取 min(宽,高)/2 时正好是两头半圆的胶囊形。
    /// </summary>
    public static void SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
        var rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, r * 2, r * 2);
        if (rgn == IntPtr.Zero)
        {
            return;
        }

        // 成功后该区域归窗口所有，不能再删；失败才需要自己释放
        if (SetWindowRgn(hwnd, rgn, true) == 0)
        {
            _ = DeleteObject(rgn);
        }
    }

    /// <summary>去掉窗口裁剪区域，恢复整窗矩形（展开态交给 DWM 圆角）。</summary>
    public static void ClearRegion(IntPtr hwnd) => _ = SetWindowRgn(hwnd, IntPtr.Zero, true);

}
