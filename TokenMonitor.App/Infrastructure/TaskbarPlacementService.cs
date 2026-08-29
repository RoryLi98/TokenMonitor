using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace TokenMonitor.App.Infrastructure;

internal sealed record TaskbarPlacementResult(
    double PositionRatio,
    bool TrafficMonitorDetected,
    bool AvoidedCollision,
    bool IsEmbedded);

internal sealed class TaskbarPlacementService
{
    private const int EdgePadding = 6;
    private const int CollisionGap = 6;
    private IntPtr _embeddedWindowHandle;
    private IntPtr _embeddedTaskbarHandle;

    public TaskbarPlacementResult Place(
        Window window,
        double? preferredRatio,
        double? desiredLeftDip = null,
        int verticalOffsetDip = 0)
    {
        var snapshot = NativeTaskbarSnapshot.Capture();
        if (snapshot is null || !snapshot.Taskbar.IsHorizontal)
        {
            return PlaceFallback(window, preferredRatio);
        }

        var windowHandle = new WindowInteropHelper(window).Handle;
        var isEmbedded = AttachToTaskbar(windowHandle, snapshot.TaskbarHandle);

        var scale = snapshot.Dpi / 96d;
        var width = Math.Max(1, (int)Math.Round((window.ActualWidth > 0 ? window.ActualWidth : window.Width) * scale));
        var height = Math.Max(1, (int)Math.Round((window.ActualHeight > 0 ? window.ActualHeight : window.Height) * scale));
        var taskbar = snapshot.Taskbar;
        var minX = taskbar.Left + EdgePadding;
        var maxX = Math.Max(minX, taskbar.Right - width - EdgePadding);
        var y = taskbar.Top
            + Math.Max(0, (taskbar.Height - height) / 2)
            + (int)Math.Round(verticalOffsetDip * scale);

        var blockers = snapshot.TrafficMonitorRegions
            .Concat(snapshot.NotificationArea is { } tray ? new[] { tray } : Array.Empty<PixelRect>())
            .Select(rect => rect.Inflate(CollisionGap))
            .ToArray();

        int desiredX;
        if (desiredLeftDip is { } draggedLeft)
        {
            desiredX = (int)Math.Round(draggedLeft * scale);
        }
        else if (snapshot.TrafficMonitorRegions.Count > 0)
        {
            var leftmostTraffic = snapshot.TrafficMonitorRegions.MinBy(rect => rect.Left);
            desiredX = leftmostTraffic.Left - width - CollisionGap;
        }
        else if (preferredRatio is { } ratio)
        {
            desiredX = minX + (int)Math.Round(Math.Clamp(ratio, 0, 1) * Math.Max(0, maxX - minX));
        }
        else if (snapshot.NotificationArea is { } notificationArea)
        {
            desiredX = notificationArea.Left - width - CollisionGap;
        }
        else
        {
            desiredX = taskbar.Right - width - 240;
        }

        desiredX = Math.Clamp(desiredX, minX, maxX);
        var resolvedX = FindNearestClearX(desiredX, minX, maxX, y, width, height, blockers);
        var candidate = new PixelRect(resolvedX, y, resolvedX + width, y + height);
        var avoidedCollision = !blockers.Any(candidate.IntersectsWith);

        if (isEmbedded)
        {
            _ = MoveWindow(
                windowHandle,
                resolvedX - taskbar.Left,
                y - taskbar.Top,
                width,
                height,
                true);
        }
        else
        {
            window.Left = resolvedX / scale;
            window.Top = y / scale;
        }

        var resolvedRatio = maxX == minX ? 0d : (resolvedX - minX) / (double)(maxX - minX);
        return new TaskbarPlacementResult(
            Math.Clamp(resolvedRatio, 0, 1),
            snapshot.TrafficMonitorRegions.Count > 0,
            avoidedCollision,
            isEmbedded);
    }

    public bool IsEmbedded(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        return handle != IntPtr.Zero
            && taskbarHandle != IntPtr.Zero
            && handle == _embeddedWindowHandle
            && taskbarHandle == _embeddedTaskbarHandle;
    }

    private bool AttachToTaskbar(IntPtr windowHandle, IntPtr taskbarHandle)
    {
        if (windowHandle == IntPtr.Zero || taskbarHandle == IntPtr.Zero)
        {
            return false;
        }

        if (windowHandle == _embeddedWindowHandle && taskbarHandle == _embeddedTaskbarHandle)
        {
            return true;
        }

        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(extendedStyle | WsExToolWindow));

        // WPF continuously synchronizes a top-level window's screen coordinates. Unlike
        // TrafficMonitor's native MFC dialog, leaving WS_POPUP set after SetParent makes
        // WPF reapply screen coordinates as child coordinates. Use the documented child
        // style combination so WPF and User32 agree on the coordinate space.
        var originalStyle = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        var childStyle = (originalStyle & ~WsPopup) | WsChild;
        _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(childStyle));

        SetLastErrorNative(0);
        var previousParent = SetParent(windowHandle, taskbarHandle);
        var error = Marshal.GetLastWin32Error();

        // TrafficMonitor deliberately keeps WS_POPUP when calling SetParent. For a popup,
        // GetParent/IsChild describe its owner/style semantics and cannot verify the native
        // parent field. SetParent can also return zero on success when there was no previous
        // parent, so the Win32 last-error value is the reliable failure signal here.
        var attached = previousParent != IntPtr.Zero || error == 0;
        if (attached)
        {
            _embeddedWindowHandle = windowHandle;
            _embeddedTaskbarHandle = taskbarHandle;
        }
        else
        {
            _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(originalStyle));
        }

        return attached;
    }

    private static int FindNearestClearX(
        int desiredX,
        int minX,
        int maxX,
        int y,
        int width,
        int height,
        IReadOnlyList<PixelRect> blockers)
    {
        bool IsClear(int x)
        {
            var candidate = new PixelRect(x, y, x + width, y + height);
            return blockers.All(blocker => !candidate.IntersectsWith(blocker));
        }

        if (IsClear(desiredX))
        {
            return desiredX;
        }

        for (var distance = 4; distance <= maxX - minX; distance += 4)
        {
            var left = Math.Max(minX, desiredX - distance);
            if (IsClear(left))
            {
                return left;
            }

            var right = Math.Min(maxX, desiredX + distance);
            if (IsClear(right))
            {
                return right;
            }
        }

        return desiredX;
    }

    private static TaskbarPlacementResult PlaceFallback(Window window, double? preferredRatio)
    {
        var workArea = SystemParameters.WorkArea;
        var left = preferredRatio is { } ratio
            ? workArea.Left + Math.Clamp(ratio, 0, 1) * Math.Max(0, workArea.Width - window.Width)
            : workArea.Right - window.Width - 12;
        window.Left = left;
        window.Top = workArea.Bottom - window.Height;
        return new TaskbarPlacementResult(preferredRatio ?? 1, false, false, false);
    }

    private sealed record NativeTaskbarSnapshot(
        IntPtr TaskbarHandle,
        PixelRect Taskbar,
        uint Dpi,
        PixelRect? NotificationArea,
        IReadOnlyList<PixelRect> TrafficMonitorRegions)
    {
        public static NativeTaskbarSnapshot? Capture()
        {
            var taskbarHandle = FindWindow("Shell_TrayWnd", null);
            if (taskbarHandle == IntPtr.Zero || !TryGetRect(taskbarHandle, out var taskbar))
            {
                return null;
            }

            var childHandles = new HashSet<IntPtr>();
            EnumChildWindows(taskbarHandle, (handle, _) =>
            {
                childHandles.Add(handle);
                return true;
            }, IntPtr.Zero);

            PixelRect? notificationArea = null;
            foreach (var handle in childHandles)
            {
                var className = ReadClassName(handle);
                if (string.Equals(className, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase)
                    && TryGetRect(handle, out var rect))
                {
                    notificationArea = rect;
                    break;
                }
            }

            var trafficHandles = new HashSet<IntPtr>();
            var trafficProcesses = Process.GetProcessesByName("TrafficMonitor");
            var trafficProcessIds = trafficProcesses.Select(process => process.Id).ToHashSet();

            void AddIfTrafficMonitor(IntPtr handle)
            {
                _ = GetWindowThreadProcessId(handle, out var processId);
                if (trafficProcessIds.Contains((int)processId))
                {
                    trafficHandles.Add(handle);
                }
            }

            EnumWindows((handle, _) =>
            {
                AddIfTrafficMonitor(handle);
                return true;
            }, IntPtr.Zero);

            foreach (var handle in childHandles)
            {
                AddIfTrafficMonitor(handle);
            }

            foreach (var process in trafficProcesses)
            {
                try
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        EnumThreadWindows((uint)thread.Id, (handle, _) =>
                        {
                            trafficHandles.Add(handle);
                            return true;
                        }, IntPtr.Zero);
                    }
                }
                catch
                {
                    // Taskbar child enumeration above still covers the common integration mode.
                }
                finally
                {
                    process.Dispose();
                }
            }

            var trafficRegions = trafficHandles
                .Where(IsWindowVisible)
                .Select(handle => TryGetRect(handle, out var rect) ? rect : default)
                .Where(rect => rect.Width > 0 && rect.Height > 0 && rect.IntersectsWith(taskbar))
                .Distinct()
                .ToArray();

            uint dpi;
            try
            {
                dpi = GetDpiForWindow(taskbarHandle);
            }
            catch
            {
                dpi = 96;
            }

            return new NativeTaskbarSnapshot(taskbarHandle, taskbar, dpi == 0 ? 96u : dpi, notificationArea, trafficRegions);
        }

        private static string ReadClassName(IntPtr handle)
        {
            return ReadWindowClassName(handle);
        }

        private static bool TryGetRect(IntPtr handle, out PixelRect rect)
        {
            if (GetWindowRect(handle, out var nativeRect))
            {
                rect = new PixelRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
                return true;
            }

            rect = default;
            return false;
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

    private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsHorizontal => Width >= Height;

        public bool IntersectsWith(PixelRect other) =>
            Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

        public PixelRect Inflate(int amount) =>
            new(Left - amount, Top - amount, Right + amount, Bottom + amount);
    }

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolWindow = 0x00000080L;

    private static string ReadWindowClassName(IntPtr handle)
    {
        var builder = new StringBuilder(128);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    private static extern void SetLastErrorNative(uint errorCode);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
}
