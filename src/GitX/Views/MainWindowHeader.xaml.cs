using GitX.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitX.Views;

public partial class MainWindowHeader : UserControl
{
    private bool _isThemeSelectionSyncing;

    public MainWindowHeader()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeSelector.ItemsSource = ThemeManager.Themes;
        ThemeSelector.DisplayMemberPath = nameof(ThemeDefinition.DisplayName);
        ThemeSelector.SelectedValuePath = nameof(ThemeDefinition.Key);
        SyncThemeSelection();
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var window = Window.GetWindow(this);
        if (window == null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            window.DragMove();
        }
        catch
        {
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnToggleMaximizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isThemeSelectionSyncing)
        {
            return;
        }

        if (ThemeSelector.SelectedValue is string themeKey)
        {
            ThemeManager.ApplyTheme(themeKey);
            SyncThemeSelection();
        }
    }

    private void SyncThemeSelection()
    {
        try
        {
            _isThemeSelectionSyncing = true;
            ThemeSelector.SelectedValue = ThemeManager.CurrentThemeKey;
        }
        finally
        {
            _isThemeSelectionSyncing = false;
        }
    }
}
