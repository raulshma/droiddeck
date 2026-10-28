using System.Runtime.InteropServices;
using DroidDeck.Core.Screen;

namespace DroidDeck.Screen;

/// <summary>Moves and focuses scrcpy's native top-level window without embedding it.</summary>
public sealed class Win32ScreenWindowController : IScreenWindowController
{
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShownormal = 1;
    private const int WmClose = 0x0010;
    private const string ScrcpyWindowClass = "SDL_app";

    /// <inheritdoc />
    public bool TryMove(int processId, ScreenBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var handle = FindWindow(processId);
        if (!bounds.IsValid || handle == IntPtr.Zero)
        {
            return false;
        }

        // ScreenBounds is expressed in effective pixels, while SetWindowPos takes
        // physical pixels. Scale by the target window's monitor DPI.
        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        return SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale),
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    /// <inheritdoc />
    public bool TryFocus(int processId)
    {
        var handle = FindWindow(processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(handle, SwShownormal);
        return SetForegroundWindow(handle);
    }

    /// <inheritdoc />
    public bool TryHide(int processId)
    {
        var handle = FindWindow(processId, includeHidden: true);
        return handle != IntPtr.Zero && ShowWindow(handle, SwHide);
    }

    /// <inheritdoc />
    public bool TryClose(int processId)
    {
        // The recorder hides its companion window immediately after launch, so a
        // close request must still find hidden top-level windows.
        var handle = FindWindow(processId, includeHidden: true);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr FindWindow(int processId, bool includeHidden = false)
    {
        if (processId <= 0)
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        var fallback = IntPtr.Zero;
        long fallbackArea = 0;
        EnumWindows(
            (handle, _) =>
            {
                var threadId = GetWindowThreadProcessId(handle, out var owner);
                if (threadId == 0 || owner != processId)
                {
                    return true;
                }

                if (!includeHidden && !IsWindowVisible(handle))
                {
                    return true;
                }

                if (!IsWindowRectValid(handle))
                {
                    return true;
                }

                var className = new char[64];
                var classLength = GetClassName(handle, className, className.Length);
                if (string.Equals(
                    new string(className, 0, Math.Max(0, classLength)),
                    ScrcpyWindowClass,
                    StringComparison.Ordinal))
                {
                    found = handle;
                    return false;
                }

                if (GetWindowRect(handle, out var rectangle))
                {
                    var area = (long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
                    if (area > fallbackArea)
                    {
                        fallbackArea = area;
                        fallback = handle;
                    }
                }

                return true;
            },
            IntPtr.Zero);
        return found != IntPtr.Zero ? found : fallback;
    }

    private static bool IsWindowRectValid(IntPtr handle) =>
        GetWindowRect(handle, out var rectangle) &&
        rectangle.Right > rectangle.Left &&
        rectangle.Bottom > rectangle.Top;

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
