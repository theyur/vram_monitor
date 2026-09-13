using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using VramMonitor.App.Formatting;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Model;

namespace VramMonitor.App.Tray;

public partial class TrayTooltipView : UserControl
{
    public TrayTooltipView() => InitializeComponent();
}

public sealed record TrayTooltipEntry(string Name, string Value);

/// <summary>
/// Backing model for the tray tooltip: the current top consumers (spec section 10.1).
/// </summary>
/// <remarks>
/// Ordering is stabilised by the analysis layer so small fluctuations do not reshuffle the list, but the
/// values shown here are the raw current measurements, not the smoothed ones used for ordering.
/// </remarks>
public sealed partial class TrayTooltipModel : ObservableObject
{
    [ObservableProperty]
    private string _footer = "Starting…";

    public ObservableCollection<TrayTooltipEntry> Entries { get; } = [];

    public void Apply(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Entries.Clear();
        foreach (TrayEntry entry in snapshot.Analysis.TrayTop5)
        {
            Entries.Add(new TrayTooltipEntry(entry.DisplayName, Format.BytesCompact(entry.CurrentDedicatedBytes)));
        }

        if (Entries.Count == 0)
        {
            Footer = snapshot.Health.BlockingError is { } error
                ? error
                : "No measurement yet.";
            return;
        }

        string age = Format.Age(snapshot.Analysis.LatestSampleUtc, snapshot.TakenUtc);
        Footer = string.IsNullOrEmpty(age)
            ? $"{snapshot.Gpu?.Description ?? "GPU"} · click to open"
            : $"{snapshot.Gpu?.Description ?? "GPU"}{age} · click to open";
    }
}
