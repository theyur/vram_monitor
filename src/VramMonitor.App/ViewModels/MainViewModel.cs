using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using VramMonitor.App.Charting;
using VramMonitor.App.Formatting;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.App.ViewModels;

/// <summary>
/// Presentation state for the main window.
/// </summary>
/// <remarks>
/// Receives already-analysed snapshots and does no measurement or analysis of its own, which is what keeps
/// the UI ignorant of how VRAM is measured (spec sections 3.2 and 18).
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);

    [ObservableProperty]
    private PlotModel? _plot;

    [ObservableProperty]
    private string _gpuName = "No GPU selected";

    [ObservableProperty]
    private string _statusLine = "Starting…";

    [ObservableProperty]
    private string? _blockingError;

    [ObservableProperty]
    private string? _selectedKey;

    [ObservableProperty]
    private double _otherDisplayFloorMegabytes;

    [ObservableProperty]
    private bool _hasHiddenOtherRows;

    /// <summary>
    /// Raised once the consumer rows have been replaced, so the view can put the selection back.
    /// </summary>
    /// <remarks>
    /// Every snapshot rebuilds both collections from scratch, which drops the list-box selection because the
    /// rows are new objects. Without this the highlighted row vanished on the next sample while the chart
    /// stayed dimmed, leaving a selection the user could see the effect of but not the cause.
    /// </remarks>
    public event EventHandler? RowsRebuilt;

    public ObservableCollection<ConsumerRow> Aggressive { get; } = [];

    public ObservableCollection<ConsumerRow> Other { get; } = [];

    /// <summary>Whether one application is currently isolated on the chart.</summary>
    public bool HasSelection => SelectedKey is not null;

    public MonitorSnapshot? Snapshot { get; private set; }

    public string AggressiveHeader => $"Aggressive consumers ({Aggressive.Count})";

    public string OtherHeader => $"Other consumers ({Other.Count})";

    public void ToggleExpanded(string key)
    {
        if (!_expandedKeys.Remove(key)) _expandedKeys.Add(key);
        if (Snapshot is not null) Rebuild(Snapshot);
    }

    public bool IsExpanded(string key) => _expandedKeys.Contains(key);

    partial void OnSelectedKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (Snapshot is not null) Plot = ChartBuilder.Build(Snapshot, value, _expandedKeys);
    }

    partial void OnOtherDisplayFloorMegabytesChanged(double value)
    {
        if (Snapshot is not null) Rebuild(Snapshot);
    }

    public void Apply(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        Rebuild(snapshot);
    }

    private void Rebuild(MonitorSnapshot snapshot)
    {
        AnalysisResult analysis = snapshot.Analysis;
        DateTimeOffset now = snapshot.TakenUtc;

        Aggressive.Clear();
        foreach (ApplicationView view in analysis.Aggressive) Aggressive.Add(new ConsumerRow(view, now));

        // The display floor is presentation only: it never affects retention, analysis or export.
        long floorBytes = (long)(OtherDisplayFloorMegabytes * MonitorSettings.BytesPerMegabyte);
        int hidden = 0;

        Other.Clear();
        foreach (ApplicationView view in analysis.Other)
        {
            bool belowFloor = floorBytes > 0
                              && (view.PeakDedicatedBytes ?? 0) < floorBytes
                              && view.Key != SelectedKey;

            if (belowFloor) { hidden++; continue; }
            Other.Add(new ConsumerRow(view, now));
        }

        HasHiddenOtherRows = hidden > 0;

        GpuName = snapshot.Gpu?.Description ?? "No GPU selected";
        BlockingError = snapshot.Health.BlockingError ?? UnreachableThresholdWarning(snapshot.Settings);
        StatusLine = BuildStatusLine(snapshot);
        Plot = ChartBuilder.Build(snapshot, SelectedKey, _expandedKeys);

        OnPropertyChanged(nameof(AggressiveHeader));
        OnPropertyChanged(nameof(OtherHeader));

        RowsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Warns when the aggressive threshold can never be reached.
    /// </summary>
    /// <remarks>
    /// Cumulative aggressive time is measured over the history window, so a threshold longer than that window
    /// is unreachable by construction and the Aggressive list would sit permanently empty with no explanation.
    /// Both values are legal on their own, so this is a warning rather than a clamp.
    /// </remarks>
    private static string? UnreachableThresholdWarning(MonitorSettings settings) =>
        settings.AggressiveDurationThreshold >= settings.HistoryWindow
            ? $"The aggressive threshold ({settings.AggressiveDurationThreshold.TotalMinutes:0.#} min) is not " +
              $"shorter than the history window ({settings.HistoryWindow.TotalMinutes:0.#} min), so no " +
              "application can ever accumulate enough qualifying time. Lower the threshold or widen the window."
            : null;

    private static string BuildStatusLine(MonitorSnapshot snapshot)
    {
        MonitorHealth health = snapshot.Health;

        string interval = $"every {snapshot.Settings.SampleInterval.TotalSeconds:0}s";
        string last = health.LastSuccessfulSampleUtc is null
            ? "no successful sample yet"
            : $"last sample {Format.Time(health.LastSuccessfulSampleUtc)}";
        string errors = health.FailedProbeCount == 0
            ? "no probe errors"
            : $"{health.FailedProbeCount} probe error(s)";
        string partial = health.PartialProbeCount > 0 ? $", {health.PartialProbeCount} partial" : string.Empty;
        string probe = $"probe {health.LastProbeDuration.TotalMilliseconds:0.0} ms";
        string window = $"window {snapshot.Settings.HistoryWindow.TotalMinutes:0} min";

        return $"{interval} · {last} · {errors}{partial} · {probe} · {window} · {snapshot.Samples.Count} samples";
    }
}
