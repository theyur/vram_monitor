using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private string _totalValue = string.Empty;

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
            TotalValue = string.Empty;
            Footer = snapshot.Health.BlockingError is { } error
                ? error
                : "No measurement yet.";
            return;
        }

        TotalValue = LatestTotal(snapshot.Analysis.Total);

        string age = Format.Age(snapshot.Analysis.LatestSampleUtc, snapshot.TakenUtc);
        Footer = $"{snapshot.Gpu?.Description ?? "GPU"}{age}";
    }

    /// <summary>
    /// The newest adapter total, or unknown when the latest probe could not read it.
    /// </summary>
    /// <remarks>
    /// Read from the adapter counter, never summed from the rows above: Windows attributes a shared surface
    /// to every process referencing it, so those rows can legitimately add up to more than this.
    /// </remarks>
    private static string LatestTotal(TotalSeries total) =>
        total.Points.Count > 0 && total.Points[^1] is { HasValue: true } latest
            ? Format.BytesCompact(latest.Value)
            : Format.Unknown;
}
