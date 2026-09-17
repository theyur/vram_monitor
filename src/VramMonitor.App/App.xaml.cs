using System.IO;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Win32;
using VramMonitor.App.Tray;
using VramMonitor.App.ViewModels;
using VramMonitor.App.Views;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Export;
using VramMonitor.Core.Model;
using VramMonitor.Core.Sampling;
using VramMonitor.Windows.Dxgi;
using VramMonitor.Windows.Pdh;
using VramMonitor.Windows.Processes;
using VramMonitor.Windows.Startup;

namespace VramMonitor.App;

/// <summary>
/// Composition root and tray host.
/// </summary>
/// <remarks>
/// The application normally lives in the system tray; the main window is opened on demand. There is no
/// service and no background process -- a single desktop process holds everything, and the monitoring
/// history exists only in its memory (spec sections 3.1 and 10).
/// </remarks>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\VramMonitor.SingleInstance";

    private readonly MainViewModel _mainModel = new();
    private readonly TrayTooltipModel _tooltipModel = new();
    private readonly AutostartManager _autostart = new();
    private readonly DxgiAdapterEnumerator _adapters = new();

    private Mutex? _instanceMutex;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private SamplerService? _sampler;
    private SettingsStore _settingsStore = SettingsStore.Default();
    private MonitorSettings _settings = new();
    private GpuInfo? _gpu;
    private bool _firstRunGpuPersisted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: two samplers would double the probing for no benefit, and two tray icons
        // would be simply confusing.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ExpansionConverter.Model = _mainModel;

        CommandLineOverrides overrides = CommandLineOverrides.Parse(e.Args);
        SettingsLoadResult loaded = _settingsStore.Load();
        _settings = overrides.ApplyTo(loaded.Settings);
        _mainModel.OtherDisplayFloorMegabytes =
            _settings.OtherListDisplayFloorBytes / (double)MonitorSettings.BytesPerMegabyte;

        string? startupProblem = loaded.Problem;

        var provider = new PdhGpuMemoryProvider(_adapters);
        startupProblem ??= PdhGpuMemoryProvider.CheckAvailability();

        IReadOnlyList<GpuInfo> available = _adapters.Enumerate();
        GpuResolution resolution = GpuSelectorResolver.Resolve(available, _settings.SelectedGpu);
        _gpu = GpuSelectorResolver.FromCommandLine(available, overrides.Gpu) ?? resolution.Gpu;

        // A resolution problem only matters if it actually left us without a GPU: an explicit --gpu can
        // succeed while a stale persisted selector fails, and complaining then would be simply wrong.
        if (_gpu is null) startupProblem ??= resolution.Problem;

        _sampler = new SamplerService(provider, new WindowsProcessMetadataResolver(), _settings);
        _sampler.SetGpu(_gpu, startupProblem);
        _sampler.SnapshotPublished += OnSnapshotPublished;

        CreateTray();

        if (!overrides.StartHidden) ShowMainWindow();

        _sampler.Start();
    }

    private void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "VRAM Monitor",
            TrayToolTip = new TrayTooltipView { DataContext = _tooltipModel },
            ContextMenu = BuildContextMenu(),
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute)),
        };

        // H.NotifyIcon 2.4.1 leaves UseStandardTooltip set even once a custom TrayToolTip has been resolved,
        // so the icon keeps the NIF_SHOWTIP flag. The shell then draws the plain ToolTipText itself and never
        // sends NIN_POPUPOPEN, which is the message the rich WPF tooltip opens on -- hovering showed only
        // "VRAM Monitor". Clearing the flag before the icon is created hands tooltip display back to WPF.
        // ToolTipText is deliberately kept as the fallback for shells that do not support the popup.
        _tray.TrayIcon.UseStandardTooltip = false;

        _tray.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _tray.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(Menu("Open", ShowMainWindow));
        menu.Items.Add(Menu("Export…", Export));
        menu.Items.Add(Menu("Settings…", OpenSettings));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Menu("Exit", ExitApplication));

        return menu;

        static System.Windows.Controls.MenuItem Menu(string header, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>
    /// Marshals each published snapshot onto the UI thread.
    /// </summary>
    /// <remarks>
    /// The sampler runs on a background task and never touches a UI object; the UI only ever receives
    /// finished, immutable state (spec section 18).
    /// </remarks>
    private void OnSnapshotPublished(MonitorSnapshot snapshot)
    {
        TryRecoverLostAdapter(snapshot);
        PersistFirstRunGpu(snapshot);

        Dispatcher.InvokeAsync(() =>
        {
            _mainModel.Apply(snapshot);
            _tooltipModel.Apply(snapshot);
        });
    }

    /// <summary>
    /// Re-resolves the monitored GPU after a driver reset gave it a new LUID.
    /// </summary>
    /// <remarks>
    /// Without this the provider would keep failing against the stale LUID forever, showing a probe error on
    /// every tick until the application was restarted. The change is queued, not applied here, because this
    /// runs on the sampler's thread and the store belongs to its loop. Since the adapter resolves to the same
    /// selector, history survives.
    /// </remarks>
    private void TryRecoverLostAdapter(MonitorSnapshot snapshot)
    {
        if (_sampler is null || _gpu is null) return;

        GpuSample? newest = snapshot.Samples.Count > 0 ? snapshot.Samples[^1] : null;
        if (newest?.Outcome is not ProbeOutcome.Failed) return;
        if (newest.FailureReason?.Contains("no longer present", StringComparison.OrdinalIgnoreCase) != true) return;

        GpuResolution resolution = GpuSelectorResolver.Resolve(_adapters.Enumerate(), _gpu.Selector);
        if (resolution.Gpu is null || resolution.Gpu.Id == _gpu.Id) return;

        _gpu = resolution.Gpu;
        _sampler.RequestGpuChange(resolution.Gpu);
    }

    /// <summary>
    /// Writes the auto-chosen GPU to settings once it has actually produced a sample.
    /// </summary>
    /// <remarks>
    /// Until a selector is persisted there is nothing to detect a missing card against, so a first run that
    /// picked the discrete GPU would silently fall back to an integrated one later. Saving only after a
    /// working probe avoids persisting a choice that does not work.
    /// </remarks>
    private void PersistFirstRunGpu(MonitorSnapshot snapshot)
    {
        if (_firstRunGpuPersisted || _gpu is null || _settings.SelectedGpu is not null) return;
        if (snapshot.Health.LastSuccessfulSampleUtc is null) return;

        _firstRunGpuPersisted = true;
        _settings = _settings with { SelectedGpu = _gpu.Selector };

        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting the default is a convenience; failing to do so must not stop monitoring.
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_mainModel);
            _window.ExportRequested += (_, _) => Export();
            _window.SettingsRequested += (_, _) => OpenSettings();
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();

        if (_sampler is not null) _mainModel.Apply(_sampler.Current);
    }

    private void Export()
    {
        if (_sampler is null) return;

        // Taken once, up front: the snapshot is immutable, so monitoring carries on untouched while the
        // files are written (spec section 17).
        MonitorSnapshot snapshot = _sampler.Current;

        var dialog = new SaveFileDialog
        {
            Title = "Export retained history",
            FileName = $"vram-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = "JSON and CSV (*.json)|*.json",
            DefaultExt = ".json",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            string directory = Path.GetDirectoryName(dialog.FileName) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(dialog.FileName);

            using (FileStream json = File.Create(Path.Combine(directory, baseName + ".json")))
            {
                JsonExporter.Write(snapshot, json);
            }

            File.WriteAllText(
                Path.Combine(directory, CsvExporter.ObservationsFileName(baseName)),
                CsvExporter.WriteObservations(snapshot));

            File.WriteAllText(
                Path.Combine(directory, CsvExporter.ApplicationsFileName(baseName)),
                CsvExporter.WriteApplications(snapshot));

            MessageBox.Show(
                _window!,
                $"Exported {snapshot.Samples.Count} samples to:\n\n{baseName}.json\n" +
                $"{CsvExporter.ObservationsFileName(baseName)}\n{CsvExporter.ApplicationsFileName(baseName)}",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(_window!, $"Export failed: {ex.Message}", "Export",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSettings()
    {
        if (_sampler is null) return;
        ShowMainWindow();

        // The checkbox reads the Run key, not the settings file, so it shows what is actually registered.
        var dialog = new SettingsWindow(_settings, _adapters.Enumerate(), _gpu, _autostart.IsEnabled())
        {
            Owner = _window,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;

        _settings = result.Settings;
        _settingsStore.Save(_settings);

        string? autostartProblem = _autostart.Set(result.StartWithWindows, Environment.ProcessPath ?? string.Empty);

        if (result.Gpu is not null) _sampler.RequestGpuChange(result.Gpu, autostartProblem);
        _gpu = result.Gpu ?? _gpu;

        _mainModel.OtherDisplayFloorMegabytes =
            _settings.OtherListDisplayFloorBytes / (double)MonitorSettings.BytesPerMegabyte;

        // Runtime-safe settings take effect at once and re-evaluate the history already in memory, rather
        // than needing a restart (spec section 14).
        _sampler.UpdateSettings(_settings);
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sampler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
