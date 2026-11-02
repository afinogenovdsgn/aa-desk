using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DownloadsStack.Localization;

namespace DownloadsStack.Tests;

[Collection("Localization")]
public sealed class SettingsNavigationTests
{
    [Fact]
    public void SidebarOpensTheRealModulePages()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/DownloadsStack;component/Theme.xaml")
                    });
                }
                Loc.Use("ru");
                var model = new MainViewModel(Dispatcher.CurrentDispatcher);
                var window = new SettingsWindow(model);
                window.Show();
                Assert.Equal(Visibility.Visible, Page(window, "General"));
                Navigate(window, "Smart");
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, Page(window, "Smart"));
                Assert.Equal(Visibility.Collapsed, Page(window, "General"));
                Assert.Equal("Win+Shift+E", ((Button)window.FindName("SmartHotkeyButton")).Content);
                Assert.True(((CheckBox)window.FindName("SmartExplorerFallback")).IsChecked);
                var info = (Border)window.FindName("SmartFallbackInfo");
                Assert.True(info.ActualWidth >= 20);
                Assert.Contains("последний", (string)ToolTipService.GetToolTip(info));
                Navigate(window, "Downloads");
                Assert.Equal(Visibility.Visible, Page(window, "Downloads"));
                Navigate(window, "About");
                Assert.Equal(Visibility.Visible, Page(window, "About"));
                Assert.Equal(Visibility.Collapsed, Page(window, "Downloads"));
                window.Close(); model.Dispose();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static Visibility Page(SettingsWindow window, string name) =>
        ((StackPanel)window.FindName(name + "Page")).Visibility;
    private static void Navigate(SettingsWindow window, string name) =>
        ((Button)window.FindName(name + "Nav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
