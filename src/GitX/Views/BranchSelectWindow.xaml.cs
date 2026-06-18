using System.Windows;
using System.Windows.Input;

namespace GitX.Views;

public partial class BranchSelectWindow : ThemedWindow
{
    public BranchSelectWindow()
    {
        InitializeComponent();
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        HandleChromeMouseDown(sender, e);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseWindow();
    }
}
