using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MailWidget;

// Keeps the WPF window in the desktop's z-order instead of treating it like
// an ordinary application window. Normal windows can cover it, while it is
// still interactive when the desktop is visible.
internal static class DesktopWindowHost
{
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const long WsExAppwindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmSpawnWorker = 0x052C;
    private const uint SmtoNormal = 0x0000;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    public static bool TryGetCursorPosition(out Point position)
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var nativePoint))
        {
            position = new Point();
            return false;
        }

        position = new Point(nativePoint.X, nativePoint.Y);
        return true;
    }

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var desktopHost = FindDesktopHost();
        if (desktopHost == IntPtr.Zero || !GetWindowRect(windowHandle, out var windowRect))
        {
            return;
        }

        try
        {
            // Once the window is already a child of the desktop host, only
            // refresh its position. Repeating SetParent while Chrome or
            // another application takes focus can make a transparent WPF
            // window disappear on some Windows configurations.
            if (GetParent(windowHandle) == desktopHost)
            {
                var currentTopLeft = new NativePoint(windowRect.Left, windowRect.Top);
                if (!ScreenToClient(desktopHost, ref currentTopLeft))
                {
                    currentTopLeft.X = 0;
                    currentTopLeft.Y = 0;
                }

                SetWindowPos(
                    windowHandle,
                    HwndTop,
                    currentTopLeft.X,
                    currentTopLeft.Y,
                    windowRect.Right - windowRect.Left,
                    windowRect.Bottom - windowRect.Top,
                    SwpNoActivate | SwpShowWindow);
                return;
            }

            var style = GetWindowLongPtrCompat(windowHandle, GwlStyle).ToInt64();
            var previousStyle = style;
            style = (style & ~WsPopup) | WsChild;
            SetWindowLongPtrCompat(windowHandle, GwlStyle, new IntPtr(style));

            var extendedStyle = GetWindowLongPtrCompat(windowHandle, GwlExstyle).ToInt64();
            var previousExtendedStyle = extendedStyle;
            extendedStyle = (extendedStyle | WsExToolwindow) & ~WsExAppwindow;
            SetWindowLongPtrCompat(windowHandle, GwlExstyle, new IntPtr(extendedStyle));

            // SetParent makes the widget a real child of the desktop host,
            // instead of merely assigning it a desktop owner. This is what
            // makes it behave like an object placed on the wallpaper.
            SetParent(windowHandle, desktopHost);
            if (GetParent(windowHandle) != desktopHost)
            {
                SetWindowLongPtrCompat(windowHandle, GwlStyle, new IntPtr(previousStyle));
                SetWindowLongPtrCompat(windowHandle, GwlExstyle, new IntPtr(previousExtendedStyle));
                return;
            }

            var topLeft = new NativePoint(windowRect.Left, windowRect.Top);
            if (!ScreenToClient(desktopHost, ref topLeft))
            {
                topLeft.X = 0;
                topLeft.Y = 0;
            }

            SetWindowPos(
                windowHandle,
                HwndTop,
                topLeft.X,
                topLeft.Y,
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        }
        catch (DllNotFoundException)
        {
            // The normal WPF window remains usable if the desktop API is not
            // available on a particular Windows configuration.
        }
        catch (EntryPointNotFoundException)
        {
            // Same fallback for older or unusual Windows environments.
        }
    }

    private static IntPtr FindDesktopHost()
    {
        var programManager = FindWindow("Progman", null);
        if (programManager == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        _ = SendMessageTimeout(
            programManager,
            WmSpawnWorker,
            IntPtr.Zero,
            IntPtr.Zero,
            SmtoNormal,
            1000,
            out _);

        var workerWindow = IntPtr.Zero;
        EnumWindows((topLevelWindow, _) =>
        {
            var shellView = FindWindowEx(topLevelWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                workerWindow = FindWindowEx(IntPtr.Zero, topLevelWindow, "WorkerW", null);
            }

            return workerWindow == IntPtr.Zero;
        }, IntPtr.Zero);

        return workerWindow == IntPtr.Zero ? programManager : workerWindow;
    }

    private static IntPtr GetWindowLongPtrCompat(IntPtr windowHandle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));
    }

    private static IntPtr SetWindowLongPtrCompat(IntPtr windowHandle, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr parentHandle,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", EntryPoint = "SetParent", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr childWindow, IntPtr newParentWindow);

    [DllImport("user32.dll", EntryPoint = "GetParent", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect windowRect);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);
}
