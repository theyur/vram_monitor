using System.Globalization;
using System.Text;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Export;

/// <summary>
/// CSV export for convenient analysis (spec section 17.2).
/// </summary>
/// <remarks>
/// <para>
/// Two files, never mixed row types: one row per sample and session observation, and a separate file of
/// per-application summaries.
/// </para>
/// <para>
/// <b>Every value is formatted with <see cref="CultureInfo.InvariantCulture"/>.</b> This is not cosmetic: on
/// a machine whose decimal separator is a comma -- the development machine is one -- culture-sensitive
/// formatting would emit "12,5" into a comma-delimited file and silently split one value across two columns.
/// </para>
/// <para>
/// A missing measurement is written as an empty field, never as zero (spec section 5.2).
/// </para>
/// </remarks>
public static class CsvExporter
{
    public static string ObservationsFileName(string baseName) => $"{baseName}-observations.csv";

    public static string ApplicationsFileName(string baseName) => $"{baseName}-applications.csv";

    /// <summary>One row per sample and observed process session.</summary>
    public static string WriteObservations(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',',
            "timestamp_utc", "probe_outcome", "application_key", "display_name", "executable_path",
            "session_id", "pid", "process_start_utc", "dedicated_bytes", "shared_bytes",
            "total_dedicated_bytes"));

        foreach (GpuSample sample in snapshot.Samples)
        {
            // A failed probe still earns a row, so the gap is visible in the exported data rather than
            // looking like a period when nothing was running.
            if (sample.Observations.Count == 0)
            {
                sb.AppendLine(string.Join(',',
                    Quote(Timestamp(sample.TimestampUtc)), Quote(sample.Outcome.ToString()),
                    "", "", "", "", "", "", "", "", Number(sample.TotalDedicatedBytes)));
                continue;
            }

            foreach (ProcessObservation observation in sample.Observations)
            {
                snapshot.Sessions.TryGetValue(observation.SessionId, out ProcessSessionInfo? session);

                sb.AppendLine(string.Join(',',
                    Quote(Timestamp(sample.TimestampUtc)),
                    Quote(sample.Outcome.ToString()),
                    Quote(session?.Identity.Key),
                    Quote(session?.Identity.DisplayName),
                    Quote(session?.Identity.ExecutablePath),
                    Number(observation.SessionId.Value),
                    Number(session?.Pid),
                    Quote(session?.ProcessStartUtc is { } start ? Timestamp(start) : null),
                    Number(observation.DedicatedBytes),
                    Number(observation.SharedBytes),
                    Number(sample.TotalDedicatedBytes)));
            }
        }

        return sb.ToString();
    }

    /// <summary>One row per application, carrying the derived figures.</summary>
    public static string WriteApplications(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',',
            "application_key", "display_name", "executable_path", "is_aggressive",
            "current_dedicated_bytes", "average_dedicated_bytes", "peak_dedicated_bytes",
            "cumulative_aggressive_seconds", "session_count"));

        foreach (Analysis.ApplicationView app in snapshot.Analysis.AllConsumers)
        {
            sb.AppendLine(string.Join(',',
                Quote(app.Key),
                Quote(app.DisplayName),
                Quote(app.ExecutablePath),
                app.IsAggressive ? "true" : "false",
                Number(app.CurrentDedicatedBytes),
                Number(app.AverageDedicatedBytes),
                Number(app.PeakDedicatedBytes),
                app.CumulativeAggressiveTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                Number(app.SessionCount)));
        }

        return sb.ToString();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(uint? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting. Executable paths can legitimately contain commas and quotes.</summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
