using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.App.Views;

/// <summary>Result of the settings dialog.</summary>
public sealed record SettingsResult(MonitorSettings Settings, GpuInfo? Gpu, bool StartWithWindows);

public partial class SettingsWindow : Window
{
    private readonly IReadOnlyList<GpuInfo> _adapters;

    public SettingsWindow(MonitorSettings settings, IReadOnlyList<GpuInfo> adapters, GpuInfo? current)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

        InitializeComponent();

        GpuBox.ItemsSource = adapters;
        GpuBox.SelectedItem = adapters.FirstOrDefault(a => current is not null && a.Id == current.Id)
                              ?? adapters.FirstOrDefault();

        IntervalBox.Text = Text(settings.SampleInterval.TotalSeconds);
        WindowBox.Text = Text(settings.HistoryWindow.TotalMinutes);
        FloorBox.Text = Text(settings.MonitoringFloorBytes / (double)MonitorSettings.BytesPerMegabyte);
        AggressiveBox.Text = Text(settings.AggressiveDurationThreshold.TotalMinutes);
        GraceBox.Text = Text(settings.DemotionGracePeriod.TotalSeconds);
        ChartTopBox.Text = Text(settings.ChartTopApplications);
        DisplayFloorBox.Text = Text(settings.OtherListDisplayFloorBytes / (double)MonitorSettings.BytesPerMegabyte);
        AutostartBox.IsChecked = settings.StartWithWindows;

        Original = settings;
    }

    public MonitorSettings Original { get; }

    public SettingsResult? Result { get; private set; }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!TryRead(IntervalBox.Text, "Sample interval", out double interval)
            || !TryRead(WindowBox.Text, "History window", out double window)
            || !TryRead(FloorBox.Text, "Monitoring floor", out double floor)
            || !TryRead(AggressiveBox.Text, "Aggressive threshold", out double aggressive)
            || !TryRead(GraceBox.Text, "Demotion grace", out double grace)
            || !TryRead(ChartTopBox.Text, "Applications charted", out double chartTop)
            || !TryRead(DisplayFloorBox.Text, "Display filter", out double displayFloor))
        {
            return;
        }

        // Validated() clamps anything out of range rather than rejecting it, so the dialog cannot leave the
        // monitor in an unusable configuration.
        MonitorSettings updated = (Original with
        {
            SampleInterval = TimeSpan.FromSeconds(interval),
            HistoryWindow = TimeSpan.FromMinutes(window),
            MonitoringFloorBytes = (long)(floor * MonitorSettings.BytesPerMegabyte),
            AggressiveDurationThreshold = TimeSpan.FromMinutes(aggressive),
            DemotionGracePeriod = TimeSpan.FromSeconds(grace),
            ChartTopApplications = (int)chartTop,
            OtherListDisplayFloorBytes = (long)(displayFloor * MonitorSettings.BytesPerMegabyte),
            StartWithWindows = AutostartBox.IsChecked == true,
            SelectedGpu = (GpuBox.SelectedItem as GpuInfo)?.Selector,
        }).Validated();

        Result = new SettingsResult(updated, GpuBox.SelectedItem as GpuInfo, AutostartBox.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private bool TryRead(string text, string field, out double value)
    {
        // Accepts the machine's own decimal separator as well as the invariant one, so a user on a
        // comma-decimal locale can type either.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            if (value >= 0) return true;
        }

        ErrorText.Text = $"{field} must be a number that is not negative.";
        return false;
    }

    private static string Text(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
