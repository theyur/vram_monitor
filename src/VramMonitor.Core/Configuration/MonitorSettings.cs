using VramMonitor.Core.Model;

namespace VramMonitor.Core.Configuration;

/// <summary>
/// All tunable monitor behaviour. Immutable: a change produces a new instance, which is what lets the
/// analysis layer re-evaluate retained history without a restart (spec section 14).
/// </summary>
public sealed record MonitorSettings
{
    public const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// Floor on the sample interval. Together with <see cref="MaxHistoryWindow"/> this bounds how many
    /// samples the rolling store can hold, and therefore the cost of a single analysis pass.
    /// </summary>
    public static readonly TimeSpan MinSampleInterval = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on the history window. See <see cref="MinSampleInterval"/>.</summary>
    public static readonly TimeSpan MaxHistoryWindow = TimeSpan.FromHours(12);

    /// <summary>
    /// Floor on the aggressive-duration threshold. A threshold of zero would be met by every consumer at
    /// every sample, marking even sub-floor applications aggressive and contradicting spec section 8.
    /// </summary>
    public static readonly TimeSpan MinAggressiveDuration = TimeSpan.FromSeconds(1);

    /// <summary>How often the GPU is probed. Spec section 14 default: 10 seconds.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Rolling history retained for display, analysis and export. Spec section 14 default: 60 minutes.</summary>
    public TimeSpan HistoryWindow { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Dedicated-VRAM floor below which an application does not take part in aggressive analysis.
    /// Applied at analysis time only; sub-floor observations are still retained so that changing this
    /// value re-evaluates existing history (spec section 8).
    /// Default of 100 MB was chosen from measured data on the target machine -- see plan section 9.
    /// </summary>
    public long MonitoringFloorBytes { get; init; } = 100 * BytesPerMegabyte;

    /// <summary>Cumulative qualifying time at which an application becomes aggressive. Default: 3 minutes.</summary>
    public TimeSpan AggressiveDurationThreshold { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long an application stays in the Aggressive section after it stops meeting the threshold.
    /// Expressed in real time so it is unaffected by changes to <see cref="SampleInterval"/>
    /// (spec section 9.4).
    /// </summary>
    public TimeSpan DemotionGracePeriod { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How many applications are plotted, by peak dedicated VRAM. Decision D2.</summary>
    public int ChartTopApplications { get; init; } = 10;

    /// <summary>
    /// Presentation-only filter for the "Other consumers" list; zero disables it. Decision D1.
    /// Never affects retention, analysis, the aggressive population, or export.
    /// </summary>
    public long OtherListDisplayFloorBytes { get; init; }

    public bool StartWithWindows { get; init; }

    /// <summary>
    /// Stable identity of the monitored GPU. Null means "resolve automatically at startup".
    /// A LUID is deliberately not stored here: LUIDs are reallocated on reboot and driver reset.
    /// </summary>
    public GpuSelector? SelectedGpu { get; init; }

    // --- Tray top-5 smoothing. Not surfaced in the settings UI, but part of the settings record so
    //     that the analysis layer stays a pure function of (history, settings) and remains testable. ---

    /// <summary>Exponential-moving-average time constant used to stabilise tray ordering.</summary>
    public TimeSpan TraySmoothingTimeConstant { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Absolute part of the margin a challenger must beat to displace a tray incumbent.</summary>
    public long TrayHysteresisMarginBytes { get; init; } = 32 * BytesPerMegabyte;

    /// <summary>Relative part of the same margin.</summary>
    public double TrayHysteresisMarginFraction { get; init; } = 0.05;

    /// <summary>How many entries the tray tooltip shows. Spec section 10.1.</summary>
    public int TrayEntryCount { get; init; } = 5;

    /// <summary>
    /// Total history the store must retain. The extra grace tail exists solely so that demotion
    /// hysteresis can evaluate a trailing <see cref="HistoryWindow"/> sum at instants as old as
    /// <c>now - DemotionGracePeriod</c>. It must never make a consumer visible for longer than
    /// <see cref="HistoryWindow"/> (spec section 7).
    /// </summary>
    public TimeSpan RetentionHorizon => HistoryWindow + DemotionGracePeriod;

    /// <summary>Returns a copy with every value clamped into a supported range.</summary>
    public MonitorSettings Validated() => this with
    {
        SampleInterval = Clamp(SampleInterval, MinSampleInterval, TimeSpan.FromMinutes(5)),
        HistoryWindow = Clamp(HistoryWindow, TimeSpan.FromMinutes(1), MaxHistoryWindow),
        MonitoringFloorBytes = Math.Clamp(MonitoringFloorBytes, 0, 64L * 1024 * BytesPerMegabyte),
        AggressiveDurationThreshold = Clamp(AggressiveDurationThreshold, MinAggressiveDuration, TimeSpan.FromHours(24)),
        DemotionGracePeriod = Clamp(DemotionGracePeriod, TimeSpan.Zero, TimeSpan.FromHours(1)),
        ChartTopApplications = Math.Clamp(ChartTopApplications, 1, 50),
        OtherListDisplayFloorBytes = Math.Clamp(OtherListDisplayFloorBytes, 0, 64L * 1024 * BytesPerMegabyte),
        TraySmoothingTimeConstant = Clamp(TraySmoothingTimeConstant, TimeSpan.Zero, TimeSpan.FromMinutes(10)),
        TrayHysteresisMarginBytes = Math.Clamp(TrayHysteresisMarginBytes, 0, 4L * 1024 * BytesPerMegabyte),
        TrayHysteresisMarginFraction = Math.Clamp(TrayHysteresisMarginFraction, 0.0, 1.0),
        TrayEntryCount = Math.Clamp(TrayEntryCount, 1, 20),
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
