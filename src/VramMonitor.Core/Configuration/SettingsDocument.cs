using System.Text.Json.Serialization;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Configuration;

/// <summary>
/// The on-disk shape of the settings file, deliberately separate from <see cref="MonitorSettings"/>.
/// </summary>
/// <remarks>
/// Units are spelled out in the property names so the file stays readable and hand-editable, and so the
/// persisted schema can evolve without dragging the domain model with it. Note that no LUID is stored: LUIDs
/// are reallocated on reboot and after a driver reset, so the GPU is remembered by a stable selector instead.
/// </remarks>
public sealed class SettingsDocument
{
    public int SchemaVersion { get; set; } = 1;

    public double SampleIntervalSeconds { get; set; } = 10;
    public double HistoryWindowMinutes { get; set; } = 60;
    public double MonitoringFloorMegabytes { get; set; } = 100;
    public double AggressiveDurationMinutes { get; set; } = 3;
    public double DemotionGraceSeconds { get; set; } = 60;
    public int ChartTopApplications { get; set; } = 10;
    public double OtherListDisplayFloorMegabytes { get; set; }
    public bool StartWithWindows { get; set; }

    public GpuSelectorDocument? SelectedGpu { get; set; }

    public static SettingsDocument From(MonitorSettings settings) => new()
    {
        SampleIntervalSeconds = settings.SampleInterval.TotalSeconds,
        HistoryWindowMinutes = settings.HistoryWindow.TotalMinutes,
        MonitoringFloorMegabytes = settings.MonitoringFloorBytes / (double)MonitorSettings.BytesPerMegabyte,
        AggressiveDurationMinutes = settings.AggressiveDurationThreshold.TotalMinutes,
        DemotionGraceSeconds = settings.DemotionGracePeriod.TotalSeconds,
        ChartTopApplications = settings.ChartTopApplications,
        OtherListDisplayFloorMegabytes =
            settings.OtherListDisplayFloorBytes / (double)MonitorSettings.BytesPerMegabyte,
        StartWithWindows = settings.StartWithWindows,
        SelectedGpu = settings.SelectedGpu is null ? null : GpuSelectorDocument.From(settings.SelectedGpu),
    };

    public MonitorSettings ToSettings() => new MonitorSettings
    {
        SampleInterval = TimeSpan.FromSeconds(SampleIntervalSeconds),
        HistoryWindow = TimeSpan.FromMinutes(HistoryWindowMinutes),
        MonitoringFloorBytes = (long)(MonitoringFloorMegabytes * MonitorSettings.BytesPerMegabyte),
        AggressiveDurationThreshold = TimeSpan.FromMinutes(AggressiveDurationMinutes),
        DemotionGracePeriod = TimeSpan.FromSeconds(DemotionGraceSeconds),
        ChartTopApplications = ChartTopApplications,
        OtherListDisplayFloorBytes =
            (long)(OtherListDisplayFloorMegabytes * MonitorSettings.BytesPerMegabyte),
        StartWithWindows = StartWithWindows,
        SelectedGpu = SelectedGpu?.ToSelector(),
    }.Validated();
}

public sealed class GpuSelectorDocument
{
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public uint SubSysId { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Ordinal { get; set; }

    public static GpuSelectorDocument From(GpuSelector s) => new()
    {
        VendorId = s.VendorId,
        DeviceId = s.DeviceId,
        SubSysId = s.SubSysId,
        Description = s.Description,
        Ordinal = s.Ordinal,
    };

    public GpuSelector ToSelector() => new(VendorId, DeviceId, SubSysId, Description, Ordinal);
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsDocument))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
