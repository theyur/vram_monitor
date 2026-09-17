using VramMonitor.App.Formatting;
using VramMonitor.Core.Analysis;

namespace VramMonitor.App.ViewModels;

/// <summary>A single row in the consumer list. Pure presentation over an <see cref="ApplicationView"/>.</summary>
public sealed class ConsumerRow(ApplicationView view, DateTimeOffset now)
{
    public ApplicationView View { get; } = view;

    public string Key => View.Key;

    public string DisplayName => View.DisplayName;

    /// <summary>Unknown renders as a dash, never as zero (spec section 5.2).</summary>
    public string Current => Format.Bytes(View.CurrentDedicatedBytes);

    public string Average => Format.Bytes(View.AverageDedicatedBytes);

    public string Peak => Format.Bytes(View.PeakDedicatedBytes);

    public string AggressiveTime => Format.Duration(View.CumulativeAggressiveTime);

    public string ExecutablePath => View.ExecutablePath ?? "(path unavailable)";

    public string Shared => Format.Bytes(View.SharedBytes);

    public string ProcessCount => View.SessionCount == 1 ? "1 process" : $"{View.SessionCount} processes";

    /// <summary>Explains a dash, so the user is never left guessing whether it means zero.</summary>
    public string CurrentTooltip => View.IsCurrentUnknown
        ? "No measurement in the most recent sample: the process either exited or its value could not be read."
        : $"Measured at {Format.Time(View.CurrentAsOfUtc)}";

    public string IdentityNote => View.IdentityKind switch
    {
        Core.Model.ProcessIdentityKind.ExecutablePath => string.Empty,
        Core.Model.ProcessIdentityKind.ExecutableName =>
            "Identified by executable name only; the full path could not be read.",
        _ => "Identified by process ID only; neither path nor name could be read.",
    };

    public IReadOnlyList<SessionRow> Sessions { get; } =
        [.. view.Sessions.Select(s => new SessionRow(s, now))];
}

/// <summary>A process-lifetime row inside an expanded application.</summary>
public sealed class SessionRow(SessionView view, DateTimeOffset now)
{
    private readonly DateTimeOffset _now = now;

    public SessionView View { get; } = view;

    public string Pid => $"PID {View.Pid}";

    public string Current => Format.Bytes(View.CurrentDedicatedBytes);

    public string Shared => Format.Bytes(View.SharedBytes);

    public string Started => View.ProcessStartUtc is null
        ? "start time unavailable"
        : $"started {Format.Time(View.ProcessStartUtc)}";

    public string Seen => $"seen {Format.Time(View.FirstSeenUtc)} – {Format.Time(View.LastSeenUtc)}";

    public string Age => Format.Age(View.LastSeenUtc, _now);
}
