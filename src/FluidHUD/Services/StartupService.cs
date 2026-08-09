using Microsoft.Win32;

namespace FluidHUD.Services;

/// <summary>
/// Registers FluidHUD for the current user only. No elevation and no scheduled
/// task are required; moving the executable is handled by refreshing the value
/// on every successful settings save and application launch.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FluidHUD";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть пользовательский раздел автозапуска.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Не удалось определить путь к FluidHUD.exe.");
        }

        key.SetValue(
            ValueName,
            $"\"{executablePath}\"",
            RegistryValueKind.String);
    }
}
