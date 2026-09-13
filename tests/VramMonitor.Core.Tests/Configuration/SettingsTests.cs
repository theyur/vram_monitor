using System.Globalization;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;
using Xunit;

namespace VramMonitor.Core.Tests.Configuration;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vram-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Settings_round_trip_through_the_file()
    {
        var store = new SettingsStore(FilePath);
        MonitorSettings original = new MonitorSettings
        {
            SampleInterval = TimeSpan.FromSeconds(15),
            HistoryWindow = TimeSpan.FromMinutes(90),
            MonitoringFloorBytes = 64 * MonitorSettings.BytesPerMegabyte,
            AggressiveDurationThreshold = TimeSpan.FromMinutes(5),
            DemotionGracePeriod = TimeSpan.FromSeconds(30),
            ChartTopApplications = 7,
            StartWithWindows = true,
            SelectedGpu = new GpuSelector(0x10DE, 0x2204, 0x87AF1043, "NVIDIA GeForce RTX 3090", 0),
        }.Validated();

        store.Save(original);
        SettingsLoadResult loaded = store.Load();

        Assert.False(loaded.UsedDefaults);
        Assert.Equal(original.SampleInterval, loaded.Settings.SampleInterval);
        Assert.Equal(original.HistoryWindow, loaded.Settings.HistoryWindow);
        Assert.Equal(original.MonitoringFloorBytes, loaded.Settings.MonitoringFloorBytes);
        Assert.Equal(original.ChartTopApplications, loaded.Settings.ChartTopApplications);
        Assert.True(loaded.Settings.StartWithWindows);
        Assert.Equal(original.SelectedGpu, loaded.Settings.SelectedGpu);
    }

    [Fact]
    public void The_persisted_file_never_contains_a_luid()
    {
        // LUIDs are reallocated on reboot and after a driver reset, so persisting one would silently
        // resolve to nothing -- or worse, to a different adapter -- on the next launch.
        var store = new SettingsStore(FilePath);
        store.Save(new MonitorSettings
        {
            SelectedGpu = new GpuSelector(0x10DE, 0x2204, 0, "NVIDIA GeForce RTX 3090", 0),
        }.Validated());

        string json = File.ReadAllText(FilePath);

        Assert.DoesNotContain("luid", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deviceId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_and_reports_why()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        SettingsLoadResult loaded = new SettingsStore(FilePath).Load();

        Assert.True(loaded.UsedDefaults);
        Assert.NotNull(loaded.Problem);
        Assert.Equal(TimeSpan.FromSeconds(10), loaded.Settings.SampleInterval);
        // The unreadable file is left in place for the user to inspect.
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void A_missing_file_yields_defaults_without_an_error()
    {
        SettingsLoadResult loaded = new SettingsStore(FilePath).Load();

        Assert.True(loaded.UsedDefaults);
        Assert.Null(loaded.Problem);
        Assert.Equal(TimeSpan.FromMinutes(60), loaded.Settings.HistoryWindow);
    }

    [Fact]
    public void Saving_is_atomic_and_leaves_no_temporary_file()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new MonitorSettings());

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_rather_than_rejected()
    {
        MonitorSettings clamped = new MonitorSettings
        {
            SampleInterval = TimeSpan.FromMilliseconds(1),
            HistoryWindow = TimeSpan.FromDays(30),
            ChartTopApplications = 5000,
            AggressiveDurationThreshold = TimeSpan.Zero,
        }.Validated();

        Assert.Equal(MonitorSettings.MinSampleInterval, clamped.SampleInterval);
        Assert.Equal(MonitorSettings.MaxHistoryWindow, clamped.HistoryWindow);
        Assert.Equal(50, clamped.ChartTopApplications);
        Assert.Equal(MonitorSettings.MinAggressiveDuration, clamped.AggressiveDurationThreshold);
    }

    [Fact]
    public void Retention_horizon_is_the_window_plus_the_grace_period()
    {
        // The grace tail is what lets demotion hysteresis look back far enough; see the analyzer.
        MonitorSettings s = new MonitorSettings
        {
            HistoryWindow = TimeSpan.FromMinutes(60),
            DemotionGracePeriod = TimeSpan.FromSeconds(90),
        }.Validated();

        Assert.Equal(TimeSpan.FromMinutes(61.5), s.RetentionHorizon);
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Overrides_are_parsed_and_applied()
    {
        CommandLineOverrides o = CommandLineOverrides.Parse(
            ["--interval-seconds", "5", "--history-minutes", "30", "--floor-mb", "250",
             "--chart-top-n", "3", "--gpu", "3090", "--start-hidden"]);

        Assert.True(o.StartHidden);
        Assert.Equal("3090", o.Gpu);
        Assert.Empty(o.Unrecognised);

        MonitorSettings applied = o.ApplyTo(new MonitorSettings());

        Assert.Equal(TimeSpan.FromSeconds(5), applied.SampleInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), applied.HistoryWindow);
        Assert.Equal(250 * MonitorSettings.BytesPerMegabyte, applied.MonitoringFloorBytes);
        Assert.Equal(3, applied.ChartTopApplications);
    }

    [Fact]
    public void Numbers_are_parsed_invariantly_regardless_of_machine_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            CommandLineOverrides o = CommandLineOverrides.Parse(["--floor-mb", "12.5"]);

            Assert.Equal(12.5, o.FloorMegabytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Unset_overrides_leave_persisted_settings_alone()
    {
        MonitorSettings persisted = new MonitorSettings { ChartTopApplications = 25 }.Validated();

        MonitorSettings applied = CommandLineOverrides.Parse(["--start-hidden"]).ApplyTo(persisted);

        Assert.Equal(25, applied.ChartTopApplications);
        Assert.Equal(persisted.HistoryWindow, applied.HistoryWindow);
    }

    [Fact]
    public void Unknown_arguments_are_surfaced_rather_than_silently_dropped()
    {
        CommandLineOverrides o = CommandLineOverrides.Parse(["--nonsense", "--interval-seconds", "5"]);

        Assert.Contains("--nonsense", o.Unrecognised);
        Assert.Equal(5, o.IntervalSeconds);
    }
}
