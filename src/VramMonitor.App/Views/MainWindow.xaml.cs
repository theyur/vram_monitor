using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VramMonitor.App.ViewModels;

namespace VramMonitor.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    private bool _restoringSelection;

    public MainWindow(MainViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;

        _model.RowsRebuilt += OnRowsRebuilt;
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

    /// <summary>
    /// Escape undoes the narrowing that is in force, innermost first: a chart selection if there is one,
    /// otherwise the window itself, which hides back to the tray exactly as closing it does.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Escape)
        {
            if (_model.HasSelection) ClearSelection();
            else Close();

            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnConsumerSelected(object sender, SelectionChangedEventArgs e)
    {
        // A null SelectedItem is never a deselection by the user: every snapshot replaces the rows, which
        // drops the selection on both lists. Clearing the model here would undo the selection on the next
        // sample. Deselection is explicit -- OnConsumerPreviewMouseDown, Escape, or the button.
        if (_restoringSelection || sender is not ListBox list || list.SelectedItem is not ConsumerRow row) return;

        // Selection is exclusive across the two lists, so highlighting is unambiguous.
        if (ReferenceEquals(list, AggressiveList)) OtherList.UnselectAll();
        else AggressiveList.UnselectAll();

        _model.SelectedKey = row.Key;
    }

    private void OnConsumerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source) return;

        // The expand toggle lives inside the row; swallowing its click would make it dead on a selected row.
        if (FindAncestor<ButtonBase>(source) is not null) return;

        if (ItemsControl.ContainerFromElement(list, source) is not ListBoxItem { DataContext: ConsumerRow row }) return;
        if (!string.Equals(row.Key, _model.SelectedKey, StringComparison.Ordinal)) return;

        ClearSelection();
        e.Handled = true;
    }

    /// <summary>
    /// Sends the wheel to the scroll viewer that actually owns the scrolling.
    /// </summary>
    /// <remarks>
    /// Taken in the tunnelling phase so the list box's own scroll viewer never sees the event: it cannot
    /// scroll -- the list is laid out at full height inside the outer viewer -- yet it would still mark the
    /// event handled and swallow it.
    /// </remarks>
    private void OnConsumerListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Handled || sender is not DependencyObject list) return;

        // Walks up, so this finds the enclosing viewer and not the list box's own, which is a descendant.
        if (FindAncestor<ScrollViewer>(list) is not { } scroller) return;

        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        _model.SelectedKey = null;
        RestoreSelection();
    }

    private void OnRowsRebuilt(object? sender, EventArgs e) => RestoreSelection();

    /// <summary>
    /// Puts the list-box highlight back on the selected application after the rows have been replaced.
    /// </summary>
    private void RestoreSelection()
    {
        _restoringSelection = true;
        try
        {
            AggressiveList.SelectedItem = FindRow(AggressiveList, _model.SelectedKey);
            OtherList.SelectedItem = FindRow(OtherList, _model.SelectedKey);
        }
        finally
        {
            _restoringSelection = false;
        }

        static ConsumerRow? FindRow(ListBox list, string? key) => key is null
            ? null
            : list.Items.OfType<ConsumerRow>().FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
    }

    private static T? FindAncestor<T>(DependencyObject from) where T : DependencyObject
    {
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match) return match;
        }

        return null;
    }

    private void OnToggleExpand(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key }) _model.ToggleExpanded(key);
    }

    private void OnExport(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
