using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VramMonitor.App.ViewModels;

namespace VramMonitor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    public MainWindow(MainViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>Raised when the user asks to export; the shell owns the file dialog and writing.</summary>
    public event EventHandler? ExportRequested;

    /// <summary>Raised when the user opens settings.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Closing hides the window rather than exiting: the application normally lives in the tray, and
    /// history is held only in RAM, so quitting on a stray close would silently discard it.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    public void ReallyClose()
    {
        Closing -= null;
        Application.Current.Shutdown();
    }

    private void OnConsumerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not ConsumerRow row) return;

        // Selection is exclusive across the two lists, so highlighting is unambiguous.
        if (ReferenceEquals(list, AggressiveList)) OtherList.UnselectAll();
        else AggressiveList.UnselectAll();

        _model.SelectedKey = row.Key;
    }

    private void OnToggleExpand(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key }) _model.ToggleExpanded(key);
    }

    private void OnExport(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
