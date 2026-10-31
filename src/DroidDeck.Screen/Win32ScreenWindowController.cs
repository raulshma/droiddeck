using System.Runtime.InteropServices;
using DroidDeck.Core.Screen;

namespace DroidDeck.Screen;

/// <summary>
/// Embeds docked scrcpy windows into the app, and moves and focuses scrcpy's
/// native window. Embedding reparents the borderless SDL window as a child of
/// the app window, so it never appears as a separate taskbar entry and can no
/// longer steal the foreground.
/// </summary>
public sealed class Win32ScreenWindowController : IScreenWindowController
{
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShownormal = 1;
    private const int WmClose = 0x0010;
    private const uint GaRoot = 2;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickframe = 0x00040000L;
    private const long WsSysmenu = 0x00080000L;
    private const long WsMinimizebox = 0x00020000L;
    private const long WsMaximizebox = 0x00010000L;
    private const long WsMinimize = 0x20000000L;
    private const long WsMaximize = 0x01000000L;
    private const long TopLevelChrome =
        WsPopup | WsCaption | WsThickframe | WsSysmenu |
        WsMinimizebox | WsMaximizebox | WsMinimize | WsMaximize;
    private const string ScrcpyWindowClass = "SDL_app";

    // Host windows by embedded scrcpy process. EnumWindows stops reporting a
    // window once it becomes a child, so later moves need the host to find it,
    // and a replacement window that scrcpy recreates can be re-embedded.
    private readonly Dictionary<int, IntPtr> _embedded = [];

    /// <inheritdoc />
    public bool TryMove(int processId, ScreenBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (!bounds.IsValid)
        {
            return false;
        }

        var handle = FindWindow(processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var parent = GetParent(handle);
        if (parent == IntPtr.Zero && _embedded.TryGetValue(processId, out var host))
        {
            // The window scrcpy recreated is still top-level; reparent it again.
            if (!Embed(handle, host))
            {
                return false;
            }

            parent = host;
        }

        return parent != IntPtr.Zero
            ? MoveChild(handle, parent, bounds)
            : MoveTopLevel(handle, bounds);
    }

    /// <inheritdoc />
    public bool TryEmbed(int processId, IntPtr host, ScreenBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (host == IntPtr.Zero || !bounds.IsValid)
        {
            return false;
        }

        var handle = FindWindow(processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var parent = GetParent(handle);
        if (parent != IntPtr.Zero)
        {
            return parent == host && MoveChild(handle, host, bounds);
        }

        if (!Embed(handle, host))
        {
            return false;
        }

        _embedded[processId] = host;
        return MoveChild(handle, host, bounds);
    }

    /// <inheritdoc />
    public bool TryFocus(int processId)
    {
        var handle = FindWindow(processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (GetParent(handle) != IntPtr.Zero)
        {
            // An embedded mirror is not a foreground window of its own; raise
            // the host app and hand the keyboard to the mirror instead. The
            // focus hand-off itself is best effort, so the attempt is reported
            // by whether the window was found.
            SetForegroundWindow(GetAncestor(handle, GaRoot));
            SetFocus(handle);
            return true;
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

        _embedded.Remove(processId);
        return PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool Embed(IntPtr handle, IntPtr host)
    {
        // Reparenting keeps the popup chrome, which makes the window clip and
        // hit-test incorrectly, so strip it and mark the window a child first.
        var style = GetWindowLong(handle, GwlStyle).ToInt64();
        style &= ~TopLevelChrome;
        style |= WsChild | WsVisible;
        SetWindowLong(handle, GwlStyle, new IntPtr(style));
        SetParent(handle, host);

        // SetParent returns the previous parent, which is null for a top-level
        // window on success, so confirm the new relationship instead.
        return GetParent(handle) == host;
    }

    private static bool MoveTopLevel(IntPtr handle, ScreenBounds bounds)
    {
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

    private static bool MoveChild(IntPtr handle, IntPtr parent, ScreenBounds bounds)
    {
        // A child window is positioned inside the parent's client area, so the
        // desktop rectangle has to be rebased on the client origin first.
        var origin = new NativePoint();
        if (!ClientToScreen(parent, ref origin))
        {
            return false;
        }

        var dpi = GetDpiForWindow(parent);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        return SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(bounds.X * scale) - origin.X,
            (int)Math.Round(bounds.Y * scale) - origin.Y,
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale),
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private IntPtr FindWindow(int processId, bool includeHidden = false)
    {
        if (processId <= 0)
        {
            return IntPtr.Zero;
        }

        if (_embedded.TryGetValue(processId, out var host) && host != IntPtr.Zero)
        {
            var child = FindEmbeddedWindow(host, processId);
            if (child != IntPtr.Zero)
            {
                return child;
            }
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

    private static IntPtr FindEmbeddedWindow(IntPtr host, int processId)
    {
        // EnumWindows only reports top-level windows, so an embedded mirror has
        // to be looked up among the host's direct children.
        IntPtr fallback = IntPtr.Zero;
        long fallbackArea = 0;
        var previous = IntPtr.Zero;
        while ((previous = FindWindowEx(host, previous, null, null)) != IntPtr.Zero)
        {
            if (GetWindowThreadProcessId(previous, out var owner) == 0 || owner != processId)
            {
                continue;
            }

            var className = new char[64];
            var classLength = GetClassName(previous, className, className.Length);
            if (string.Equals(
                new string(className, 0, Math.Max(0, classLength)),
                ScrcpyWindowClass,
                StringComparison.Ordinal))
            {
                return previous;
            }

            if (GetWindowRect(previous, out var rectangle))
            {
                var area = (long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
                if (area > fallbackArea)
                {
                    fallbackArea = area;
                    fallback = previous;
                }
            }
        }

        return fallback;
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
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr handle, int index, IntPtr value);

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
    private static extern bool SetParent(IntPtr handle, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr handle);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
