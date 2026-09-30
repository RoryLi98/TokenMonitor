using Microsoft.Win32;
using System.IO;

namespace TokenMonitor.App.Infrastructure;

internal static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenMonitor";

    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");

            if (!enabled)
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
                error = null;
                return true;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("无法确定 TokenMonitor.exe 的路径。");
            }

            runKey.SetValue(
                ValueName,
                $"\"{executablePath}\" --startup",
                RegistryValueKind.String);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
