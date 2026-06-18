using System.Windows;
using System.Windows.Input;

namespace GitX.Views;

/// <summary>
/// 统一的无边框窗口基类。
/// </summary>
public class ThemedWindow : Window
{
    protected void HandleChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 拖拽时窗口状态变化会抛异常，忽略即可。
        }
    }

    protected void MinimizeWindow()
    {
        WindowState = WindowState.Minimized;
    }

    protected void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    protected void CloseWindow()
    {
        Close();
    }
}
