using VramMonitor.Core.Model;

namespace VramMonitor.Core.Sampling;

/// <summary>
/// Builds the identity an application is grouped by, following the fallback order in spec section 5.3.
/// </summary>
/// <remarks>
/// Grouping is by full executable path so that unrelated processes sharing a file name -- notably
/// <c>python.exe</c> from different environments -- never merge into one application (spec section 6.1).
/// The three tiers use disjoint key namespaces so a path-identified application can never collide with a
/// name-identified or PID-identified one.
/// </remarks>
public static class ApplicationIdentity
{
    public static ProcessIdentity FromPath(string executablePath, string? displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string trimmed = executablePath.Trim();
        string fileName = SafeFileName(trimmed);
        string name = string.IsNullOrWhiteSpace(displayName)
            ? StripExtension(fileName)
            : displayName.Trim();

        return new ProcessIdentity(
            Key: trimmed.ToLowerInvariant(),
            DisplayName: name,
            ExecutablePath: trimmed,
            ExecutableName: fileName,
            Kind: ProcessIdentityKind.ExecutablePath);
    }

    public static ProcessIdentity FromName(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        string trimmed = executableName.Trim();
        return new ProcessIdentity(
            Key: $"name:{trimmed.ToLowerInvariant()}",
            DisplayName: StripExtension(trimmed),
            ExecutablePath: null,
            ExecutableName: trimmed,
            Kind: ProcessIdentityKind.ExecutableName);
    }

    public static ProcessIdentity FromPid(uint pid) =>
        new(
            Key: $"pid:{pid}",
            DisplayName: $"PID {pid}",
            ExecutablePath: null,
            ExecutableName: null,
            Kind: ProcessIdentityKind.PidFallback);

    private static string SafeFileName(string path)
    {
        // Path.GetFileName throws on nothing in modern .NET, but a counter-derived path is untrusted input.
        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    private static string StripExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }
}
