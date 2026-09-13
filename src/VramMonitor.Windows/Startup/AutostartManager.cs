using Microsoft.Win32;

namespace VramMonitor.Windows.Startup;

/// <summary>
/// Manages the optional "Start with Windows" entry (spec section 10.2).
/// </summary>
/// <remarks>
/// Uses the per-user Run key, which a normal user can write without elevation, rather than a scheduled task
/// or a service. Spec section 3.1 rules out a Windows service, and spec section 4 asks for normal-user
/// operation by default.
/// </remarks>
public sealed class AutostartManager(string valueName = "VramMonitor")
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName = valueName;

    /// <summary>Whether the Run entry exists. Reports false when the key cannot be read at all.</summary>
    /// <remarks>
    /// This is called while opening the settings dialog, so a locked-down machine that denies the read must
    /// not stop the dialog appearing. Reporting false is the safe way to be wrong: the box shows unticked,
    /// and ticking it calls <see cref="Set"/>, which surfaces the same denial as a message the user can read.
    /// </remarks>
    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the entry. Returns null on success, or a message describing the failure.</summary>
    public string? Set(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (enabled)
            {
                // Started hidden, because the application normally lives in the tray.
                key.SetValue(_valueName, $"\"{executablePath}\" --start-hidden", RegistryValueKind.String);
            }
            else if (key.GetValue(_valueName) is not null)
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"Start with Windows could not be changed: {ex.Message}";
        }
    }
}
