using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TokenMonitor.App.Infrastructure;

internal sealed record TaskbarPlacementResult(
    double PositionRatio,
    string? MonitorDeviceName,
    bool TrafficMonitorDetected,
    bool AvoidedCollision,
    bool IsEmbedded);

internal sealed class TaskbarPlacementService
{
    private const int EdgePadding = 6;
    private const int CollisionGap = 6;

    public TaskbarPlacementResult Place(
        Window window,
        double? preferredRatio,
        double? desiredLeftDip = null,
        int verticalOffsetDip = 0,
        string? preferredMonitorDeviceName = null)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        var snapshots = NativeTaskbarSnapshot.CaptureAll()
            .Where(candidate => candidate.Taskbar.IsHorizontal)
            .ToArray();
        var hasCurrentWindowRect = TryGetRect(windowHandle, out var currentWindowRect);
        var snapshot = SelectTaskbar(
            snapshots,
            preferredMonitorDeviceName,
            desiredLeftDip is not null && hasCurrentWindowRect ? currentWindowRect : null);
        if (snapshot is null || !snapshot.Taskbar.IsHorizontal)
        {
            return PlaceFallback(window, preferredRatio);
        }

        PrepareTaskbarOverlay(windowHandle);

        var scale = snapshot.Dpi / 96d;
        // WPF owns the HWND size and already applies per-monitor DPI scaling. Feeding a
        // taskbar-scaled width/height back through SetWindowPos makes WPF adopt that size,
        // then scales it again on the next placement tick. On a non-100% DPI monitor this
        // grows the transparent window exponentially and destroys both height and dragging.
        // Always use the current native size for collision calculations and move only.
        var width = hasCurrentWindowRect && currentWindowRect.Width > 0
            ? currentWindowRect.Width
            : Math.Max(1, (int)Math.Round(window.Width * VisualTreeHelper.GetDpi(window).DpiScaleX));
        var height = hasCurrentWindowRect && currentWindowRect.Height > 0
            ? currentWindowRect.Height
            : Math.Max(1, (int)Math.Round(window.Height * VisualTreeHelper.GetDpi(window).DpiScaleY));
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
        if (desiredLeftDip is not null && hasCurrentWindowRect)
        {
            desiredX = currentWindowRect.Left;
        }
        else if (preferredRatio is { } ratio)
        {
            desiredX = minX + (int)Math.Round(Math.Clamp(ratio, 0, 1) * Math.Max(0, maxX - minX));
        }
        else if (snapshot.TrafficMonitorRegions.Count > 0)
        {
            var leftmostTraffic = snapshot.TrafficMonitorRegions.MinBy(rect => rect.Left);
            desiredX = leftmostTraffic.Left - width - CollisionGap;
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

        _ = SetWindowPos(
            windowHandle,
            HwndTopmost,
            resolvedX,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpShowWindow);

        var resolvedRatio = maxX == minX ? 0d : (resolvedX - minX) / (double)(maxX - minX);
        return new TaskbarPlacementResult(
            Math.Clamp(resolvedRatio, 0, 1),
            snapshot.MonitorDeviceName,
            snapshot.TrafficMonitorRegions.Count > 0,
            avoidedCollision,
            IsEmbedded: false);
    }

    public bool IsEmbedded(Window window) => false;

    private static NativeTaskbarSnapshot? SelectTaskbar(
        IReadOnlyList<NativeTaskbarSnapshot> snapshots,
        string? preferredMonitorDeviceName,
        PixelRect? currentWindowRect)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        if (currentWindowRect is { } draggedWindow)
        {
            var centerX = draggedWindow.Left + draggedWindow.Width / 2;
            var centerY = draggedWindow.Top + draggedWindow.Height / 2;
            return snapshots.MinBy(candidate => DistanceSquaredToRect(centerX, centerY, candidate.Taskbar));
        }

        if (!string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
        {
            var preferred = snapshots.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.MonitorDeviceName,
                    preferredMonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return snapshots.FirstOrDefault(candidate => candidate.IsPrimary) ?? snapshots[0];
    }

    private static long DistanceSquaredToRect(int x, int y, PixelRect rect)
    {
        var dx = x < rect.Left ? rect.Left - x : x > rect.Right ? x - rect.Right : 0;
        var dy = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0;
        return (long)dx * dx + (long)dy * dy;
    }

    private static void PrepareTaskbarOverlay(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(
            windowHandle,
            GwlExStyle,
            new IntPtr(extendedStyle | WsExToolWindow | WsExNoActivate));

        // Windows 11 renders most of the taskbar through a compositor surface. A real
        // WS_CHILD can be reported as visible while remaining underneath that surface.
        // TrafficMonitor-style text therefore stays a non-activating top-level popup
        // positioned over the taskbar instead of becoming a Shell_TrayWnd child.
        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        _ = SetWindowLongPtr(
            windowHandle,
            GwlStyle,
            new IntPtr((style & ~WsChild) | WsPopup));
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
        return new TaskbarPlacementResult(preferredRatio ?? 1, null, false, false, false);
    }

    private sealed record NativeTaskbarSnapshot(
        IntPtr TaskbarHandle,
        PixelRect Taskbar,
        uint Dpi,
        string? MonitorDeviceName,
        bool IsPrimary,
        PixelRect? NotificationArea,
        IReadOnlyList<PixelRect> TrafficMonitorRegions)
    {
        public static IReadOnlyList<NativeTaskbarSnapshot> CaptureAll()
        {
            var primaryTaskbarHandle = FindWindow("Shell_TrayWnd", null);
            var taskbarHandles = new HashSet<IntPtr>();
            if (primaryTaskbarHandle != IntPtr.Zero)
            {
                taskbarHandles.Add(primaryTaskbarHandle);
            }

            EnumWindows((handle, _) =>
            {
                if (string.Equals(
                        ReadWindowClassName(handle),
                        "Shell_SecondaryTrayWnd",
                        StringComparison.OrdinalIgnoreCase))
                {
                    taskbarHandles.Add(handle);
                }

                return true;
            }, IntPtr.Zero);

            var trafficRegions = CaptureTrafficMonitorRegions(taskbarHandles);
            var snapshots = new List<NativeTaskbarSnapshot>();
            foreach (var taskbarHandle in taskbarHandles)
            {
                if (!TryGetRect(taskbarHandle, out var taskbar))
                {
                    continue;
                }

                snapshots.Add(CaptureTaskbar(
                    taskbarHandle,
                    taskbar,
                    taskbarHandle == primaryTaskbarHandle,
                    trafficRegions));
            }

            return snapshots;
        }

        private static NativeTaskbarSnapshot CaptureTaskbar(
            IntPtr taskbarHandle,
            PixelRect taskbar,
            bool isPrimary,
            IReadOnlyList<PixelRect> trafficRegions)
        {
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

            uint dpi;
            try
            {
                dpi = GetDpiForWindow(taskbarHandle);
            }
            catch
            {
                dpi = 96;
            }

            return new NativeTaskbarSnapshot(
                taskbarHandle,
                taskbar,
                dpi == 0 ? 96u : dpi,
                ReadMonitorDeviceName(taskbarHandle),
                isPrimary,
                notificationArea,
                trafficRegions.Where(rect => rect.IntersectsWith(taskbar)).ToArray());
        }

        private static IReadOnlyList<PixelRect> CaptureTrafficMonitorRegions(
            IReadOnlyCollection<IntPtr> taskbarHandles)
        {
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

            foreach (var taskbarHandle in taskbarHandles)
            {
                EnumChildWindows(taskbarHandle, (handle, _) =>
                {
                    AddIfTrafficMonitor(handle);
                    return true;
                }, IntPtr.Zero);
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

            return trafficHandles
                .Where(IsWindowVisible)
                .Select(handle => TryGetRect(handle, out var rect) ? rect : default)
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .Distinct()
                .ToArray();
        }

        private static string? ReadMonitorDeviceName(IntPtr windowHandle)
        {
            var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty,
            };
            try
            {
                return GetMonitorInfo(monitor, ref info) ? info.DeviceName : null;
            }
            catch
            {
                return null;
            }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
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
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static string ReadWindowClassName(IntPtr handle)
    {
        var builder = new StringBuilder(128);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
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
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfoEx monitorInfo);
}
