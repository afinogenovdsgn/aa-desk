using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DownloadsStack.Localization;
using DownloadsStack.Models;
using DownloadsStack.Services;
using Microsoft.Win32;

namespace DownloadsStack;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _model;
    private readonly UpdateService _updates;
    private string? _selection;
    private bool _busy;
    private bool _footerChecked;
    private Button? _activeNav;
    // Dragging a slider walks through every value on the way; writing settings.json at each of them
    // would be a hundred files for one gesture. The flyout follows the thumb, the disk follows the pause.
    private readonly DispatcherTimer _sliderSave;
    private int? _pendingBackdrop, _pendingCount;
    internal SettingsWindow(MainViewModel model, UpdateService? updates = null)
    {
        InitializeComponent(); _model = model; _updates = updates ?? new UpdateService(); DataContext = model;
        _activeNav = GeneralNav;
        _updates.PropertyChanged += UpdatesChanged;
        RefreshUpdates();
        // Windows owns this answer, and Task Manager can change it behind the application's back.
        Loaded += async (_, _) => await model.RefreshAutostartAsync();
        // Rounded corners and the dark non-client frame; the panel itself is painted opaque.
        SourceInitialized += (_, _) => MainWindow.ApplyWindowAppearance(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        FolderList.SelectionChanged += (_, _) =>
        {
            if (FolderList.SelectedItem is SourceStatus status) _selection = status.Source.Id;
            RemoveButton.IsEnabled = FolderList.SelectedItem is not null;
        };
        model.Sources.CollectionChanged += SourcesChanged;
        _sliderSave = new(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveSliders(), Dispatcher);
        _sliderSave.Stop();
        Closed += (_, _) =>
        {
            model.Sources.CollectionChanged -= SourcesChanged;
            _updates.PropertyChanged -= UpdatesChanged;
            SaveSliders(); // Closing within the pause must still keep what the sliders were left at.
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    private void SourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { if (_selection is not null) FolderList.SelectedItem = _model.Sources.FirstOrDefault(s => s.Source.Id == _selection); }
    private async Task ExecuteAsync(Func<Task<string?>> action)
    {
        if (_busy) return;
        _busy = true; Actions.IsEnabled = false; SmartExplorerModule.IsEnabled = false; DownloadsModule.IsEnabled = false; Message.Text = "";
        try { _selection = await action(); SourcesChanged(null, new(NotifyCollectionChangedAction.Reset)); }
        catch (Exception ex) { Services.LocalLog.Write("Settings", ex); Message.Text = Loc.T("Settings_SaveFailed", ex.Message); }
        finally { _busy = false; Actions.IsEnabled = true; SmartExplorerModule.IsEnabled = true; DownloadsModule.IsEnabled = true; }
    }
    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("Settings_PickFolder"), Multiselect = false };
        if (picker.ShowDialog(this) == true) await ExecuteAsync(() => _model.AddDirectoryAsync(picker.FolderName));
    }
    private async void RemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not SourceStatus source) return;
        await ExecuteAsync(async () => { await _model.RemoveSourceAsync(source.Source.Id); return null; });
    }
    private async void AddDownloads(object sender, RoutedEventArgs e) => await ExecuteAsync(_model.AddDownloadsAsync);
    private async void HardwareChanged(object sender, RoutedEventArgs e)
    {
        // The binding sets the box while the window is being built; only a real click is a change to save.
        if (!IsLoaded) return;
        await ExecuteAsync(async () => { await _model.SetHardwareRenderingAsync(Hardware.IsChecked == true); return _selection; });
    }
    private async void RunAtLogonChanged(object sender, RoutedEventArgs e)
    {
        // The binding sets the box while the window is being built; only a real click is a change to make.
        if (!IsLoaded) return;
        await ExecuteAsync(async () => { await _model.SetRunAtLogonAsync(RunAtLogon.IsChecked == true); return _selection; });
    }
    private void SortFieldChanged(object sender, SelectionChangedEventArgs e) => SaveSort();
    private void SortReverseChanged(object sender, RoutedEventArgs e) => SaveSort();
    private async void SaveSort()
    {
        // The bindings set both controls while the window is being built; only a real choice is a change to save.
        if (!IsLoaded || SortBox.SelectedValue is not SortField field) return;
        await ExecuteAsync(async () => { await _model.SetSortAsync(field, SortReverse.IsChecked == true); return _selection; });
    }
    private void BackdropChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The binding sets the slider while the window is being built; only a real drag is a change to save.
        if (!IsLoaded) return;
        var percent = (int)Math.Round(e.NewValue);
        _pendingBackdrop = percent;
        _model.PreviewBackdropOpacity(percent); // The open flyout repaints under the thumb, before any save.
        _sliderSave.Stop(); _sliderSave.Start();
    }
    private void VisibleCountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The binding sets the slider while the window is being built; only a real drag is a change to save.
        if (!IsLoaded) return;
        var count = (int)Math.Round(e.NewValue);
        _pendingCount = count;
        _model.PreviewMaxVisibleItems(count); // The open flyout grows or shrinks under the thumb, before any save.
        _sliderSave.Stop(); _sliderSave.Start();
    }
    private async void SaveSliders()
    {
        _sliderSave.Stop();
        var percent = _pendingBackdrop; var count = _pendingCount;
        _pendingBackdrop = null; _pendingCount = null;
        // Not through ExecuteAsync: a folder operation in flight would drop the value on the floor, and
        // greying the buttons out mid-drag is worse than the write it saves.
        try
        {
            if (percent is { } opacity) await _model.SetBackdropOpacityAsync(opacity);
            if (count is { } rows) await _model.SetMaxVisibleItemsAsync(rows);
        }
        catch (Exception ex) { Services.LocalLog.Write("Settings", ex); Message.Text = Loc.T("Settings_SaveFailed", ex.Message); }
    }
    private void Retry(object sender, RoutedEventArgs e) => _model.Refresh();
    private async void ModuleChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not ToggleButton box) return;
        var module = box == DownloadsModule ? "downloads" : "smart-explorer";
        await ExecuteAsync(async () => { await _model.SetModuleEnabledAsync(module, box.IsChecked == true); return _selection; });
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
    private async void FallbackChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await ExecuteAsync(async () => { await _model.SetSmartExplorerFallbackAsync(SmartExplorerFallback.IsChecked == true); return _selection; });
    }
    private void UpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshUpdates();
    private void RefreshUpdates()
    {
        UpdateVersion.Text = "v" + _updates.CurrentVersion;
        AboutVersion.Text = Loc.T("Settings_Version", _updates.CurrentVersion);
        UpdateStatus.Text = _updates.Status;
        CheckUpdatesButton.IsEnabled = !_updates.IsBusy;
        InstallUpdateButton.Visibility = _updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = !_updates.IsBusy;
        FooterUpdateButton.IsEnabled = !_updates.IsBusy;
        FooterUpdateButton.Content = _updates.HasUpdate
            ? Loc.T("Settings_InstallUpdate") + " · v" + _updates.AvailableVersion
            : _updates.IsBusy ? Loc.T("Settings_CheckingUpdates")
            : !_footerChecked ? Loc.T("Settings_CheckUpdates")
            : _updates.Result switch
            {
                UpdateCheckResult.Current => Loc.T("Settings_NoUpdate"),
                UpdateCheckResult.NoRelease => Loc.T("Settings_NoRelease"),
                UpdateCheckResult.Error => Loc.T("Settings_UpdateError"),
                _ => Loc.T("Settings_CheckUpdates")
            };
    }
    private async void CheckUpdates(object sender, RoutedEventArgs e) { _footerChecked = true; await _updates.CheckAsync(); }
    private async void InstallUpdate(object sender, RoutedEventArgs e) => await _updates.DownloadAndRunAsync();
    private async void FooterUpdate(object sender, RoutedEventArgs e)
    {
        _footerChecked = true;
        if (_updates.HasUpdate) await _updates.DownloadAndRunAsync();
        else await _updates.CheckAsync();
    }
    private void FooterUpdateEnter(object sender, MouseEventArgs e) => AnimateFooterColor(Color.FromRgb(116, 211, 238));
    private void FooterUpdateLeave(object sender, MouseEventArgs e) => AnimateFooterColor(Color.FromRgb(189, 189, 189));
    private void AnimateFooterColor(Color target)
    {
        if (FooterUpdateButton.Foreground is not SolidColorBrush brush || brush.IsFrozen)
        {
            brush = new SolidColorBrush(Color.FromRgb(189, 189, 189));
            FooterUpdateButton.Foreground = brush;
        }
        if (!SystemParameters.ClientAreaAnimation) { brush.Color = target; return; }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
    private void ShowPage(StackPanel page, Button nav)
    {
        GeneralPage.Visibility = page == GeneralPage ? Visibility.Visible : Visibility.Collapsed;
        SmartPage.Visibility = page == SmartPage ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = page == DownloadsPage ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == AboutPage ? Visibility.Visible : Visibility.Collapsed;
        if (_activeNav is not null) _activeNav.Tag = null;
        nav.Tag = "Selected";
        _activeNav = nav;
        SettingsScroll.ScrollToTop();
        if (SystemParameters.ClientAreaAnimation)
        {
            page.Opacity = 0;
            page.RenderTransform = new TranslateTransform(0, 7);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
            ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        }
    }
    private void NavigateGeneral(object sender, RoutedEventArgs e) => ShowPage(GeneralPage, GeneralNav);
    private void NavigateSmart(object sender, RoutedEventArgs e) => ShowPage(SmartPage, SmartNav);
    private void NavigateDownloads(object sender, RoutedEventArgs e) => ShowPage(DownloadsPage, DownloadsNav);
    private void NavigateAbout(object sender, RoutedEventArgs e) => ShowPage(AboutPage, AboutNav);
    private void Done(object sender, RoutedEventArgs e) => Close();
}
