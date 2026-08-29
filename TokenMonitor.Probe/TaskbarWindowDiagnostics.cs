using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class TaskbarWindowDiagnostics
{
    public static void WriteToConsole()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var targetProcessIds = Process.GetProcesses()
            .Where(process => process.ProcessName is "TokenMonitor" or "TrafficMonitor")
            .ToDictionary(process => process.Id, process => process.ProcessName);

        var windows = new List<object>();
        var addedHandles = new HashSet<IntPtr>();
        _ = GetWindowRect(taskbar, out var taskbarRect);

        bool AddTargetWindow(IntPtr handle)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (!targetProcessIds.TryGetValue((int)processId, out var processName)
                || !addedHandles.Add(handle))
            {
                return true;
            }

            _ = GetWindowRect(handle, out var rect);
            var parent = GetParent(handle);
            windows.Add(new
            {
                processName,
                handle = handle.ToInt64(),
                title = ReadWindowText(handle),
                className = ReadClassName(handle),
                parentHandle = parent.ToInt64(),
                parentClassName = ReadClassName(parent),
                isChildOfTaskbar = IsChild(taskbar, handle),
                visible = IsWindowVisible(handle),
                rect = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
            });
            return true;
        }

        EnumWindows((handle, parameter) => AddTargetWindow(handle), IntPtr.Zero);
        EnumChildWindows(taskbar, (handle, parameter) => AddTargetWindow(handle), IntPtr.Zero);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            taskbarHandle = taskbar.ToInt64(),
            taskbarClassName = ReadClassName(taskbar),
            taskbarRect = new { taskbarRect.Left, taskbarRect.Top, taskbarRect.Right, taskbarRect.Bottom },
            windows,
        }));
    }

    private static string ReadClassName(IntPtr handle)
    {
        var text = new StringBuilder(256);
        _ = GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }

    private static string ReadWindowText(IntPtr handle)
    {
        var text = new StringBuilder(256);
        _ = GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder windowText, int maxCount);
}
