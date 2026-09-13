using System.Text.Json;
using System.Text.Json.Serialization;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Export;

/// <summary>
/// Canonical, lossless export of the retained window (spec section 17.1).
/// </summary>
/// <remarks>
/// A missing measurement is written as JSON <c>null</c>, never as zero, so a reader can reconstruct exactly
/// which observations were real. <c>System.Text.Json</c> formats numbers with an invariant decimal point
/// regardless of machine culture.
/// </remarks>
public static class JsonExporter
{
    public const int SchemaVersion = 1;

    public static void Write(MonitorSnapshot snapshot, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);

        JsonSerializer.Serialize(destination, Build(snapshot), ExportJsonContext.Default.ExportDocument);
    }

    public static string ToJson(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(Build(snapshot), ExportJsonContext.Default.ExportDocument);
    }

    internal static ExportDocument Build(MonitorSnapshot snapshot) => new()
    {
        SchemaVersion = SchemaVersion,
        ExportedAtUtc = snapshot.TakenUtc,
        Gpu = snapshot.Gpu is null ? null : new ExportGpu
        {
            Luid = snapshot.Gpu.Id.Luid,
            Description = snapshot.Gpu.Description,
            DedicatedVideoMemoryBytes = snapshot.Gpu.DedicatedVideoMemoryBytes,
            VendorId = snapshot.Gpu.Selector.VendorId,
            DeviceId = snapshot.Gpu.Selector.DeviceId,
        },
        Settings = new ExportSettings
        {
            SampleIntervalSeconds = snapshot.Settings.SampleInterval.TotalSeconds,
            HistoryWindowMinutes = snapshot.Settings.HistoryWindow.TotalMinutes,
            MonitoringFloorBytes = snapshot.Settings.MonitoringFloorBytes,
            AggressiveDurationSeconds = snapshot.Settings.AggressiveDurationThreshold.TotalSeconds,
            DemotionGraceSeconds = snapshot.Settings.DemotionGracePeriod.TotalSeconds,
        },
        Health = new ExportHealth
        {
            LastSuccessfulSampleUtc = snapshot.Health.LastSuccessfulSampleUtc,
            FailedProbeCount = snapshot.Health.FailedProbeCount,
            PartialProbeCount = snapshot.Health.PartialProbeCount,
            SkippedCycles = snapshot.Health.SkippedCycles,
            LateCycles = snapshot.Health.LateCycles,
        },
        Sessions = [.. snapshot.Sessions.Values.Select(s => new ExportSession
        {
            SessionId = s.SessionId.Value,
            Pid = s.Pid,
            ApplicationKey = s.Identity.Key,
            DisplayName = s.Identity.DisplayName,
            ExecutablePath = s.Identity.ExecutablePath,
            IdentityKind = s.Identity.Kind.ToString(),
            ProcessStartUtc = s.ProcessStartUtc,
            FirstSeenUtc = s.FirstSeenUtc,
            LastSeenUtc = s.LastSeenUtc,
        })],
        Samples = [.. snapshot.Samples.Select(sample => new ExportSample
        {
            TimestampUtc = sample.TimestampUtc,
            Outcome = sample.Outcome.ToString(),
            FailureReason = sample.FailureReason,
            TotalDedicatedBytes = sample.TotalDedicatedBytes,
            Observations = [.. sample.Observations.Select(o => new ExportObservation
            {
                SessionId = o.SessionId.Value,
                DedicatedBytes = o.DedicatedBytes,
                SharedBytes = o.SharedBytes,
            })],
        })],
        Applications = [.. snapshot.Analysis.AllConsumers.Select(a => new ExportApplication
        {
            Key = a.Key,
            DisplayName = a.DisplayName,
            ExecutablePath = a.ExecutablePath,
            IsAggressive = a.IsAggressive,
            CurrentDedicatedBytes = a.CurrentDedicatedBytes,
            AverageDedicatedBytes = a.AverageDedicatedBytes,
            PeakDedicatedBytes = a.PeakDedicatedBytes,
            CumulativeAggressiveSeconds = a.CumulativeAggressiveTime.TotalSeconds,
            SessionCount = a.SessionCount,
        })],
    };
}

internal sealed class ExportDocument
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public ExportGpu? Gpu { get; set; }
    public ExportSettings Settings { get; set; } = new();
    public ExportHealth Health { get; set; } = new();
    public List<ExportSession> Sessions { get; set; } = [];
    public List<ExportSample> Samples { get; set; } = [];
    public List<ExportApplication> Applications { get; set; } = [];
}

internal sealed class ExportGpu
{
    public string Luid { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long DedicatedVideoMemoryBytes { get; set; }
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
}

internal sealed class ExportSettings
{
    public double SampleIntervalSeconds { get; set; }
    public double HistoryWindowMinutes { get; set; }
    public long MonitoringFloorBytes { get; set; }
    public double AggressiveDurationSeconds { get; set; }
    public double DemotionGraceSeconds { get; set; }
}

internal sealed class ExportHealth
{
    public DateTimeOffset? LastSuccessfulSampleUtc { get; set; }
    public int FailedProbeCount { get; set; }
    public int PartialProbeCount { get; set; }
    public int SkippedCycles { get; set; }
    public int LateCycles { get; set; }
}

internal sealed class ExportSession
{
    public long SessionId { get; set; }
    public uint Pid { get; set; }
    public string ApplicationKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string IdentityKind { get; set; } = string.Empty;
    public DateTimeOffset? ProcessStartUtc { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

internal sealed class ExportSample
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public long? TotalDedicatedBytes { get; set; }
    public List<ExportObservation> Observations { get; set; } = [];
}

internal sealed class ExportObservation
{
    public long SessionId { get; set; }

    /// <summary>Null means the measurement was missing. It never means zero.</summary>
    public long? DedicatedBytes { get; set; }

    public long? SharedBytes { get; set; }
}

internal sealed class ExportApplication
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public bool IsAggressive { get; set; }
    public long? CurrentDedicatedBytes { get; set; }
    public long? AverageDedicatedBytes { get; set; }
    public long? PeakDedicatedBytes { get; set; }
    public double CumulativeAggressiveSeconds { get; set; }
    public int SessionCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExportDocument))]
internal sealed partial class ExportJsonContext : JsonSerializerContext;
