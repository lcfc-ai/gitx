using System.Windows;
using System.Windows.Controls;

namespace GitX.Views;

public partial class ConfirmDangerDialog : Window
{
    public string ExpectedInput { get; set; } = string.Empty;
    public bool Confirmed { get; private set; }

    public ConfirmDangerDialog()
    {
        InitializeComponent();
    }

    public ConfirmDangerDialog(string title, string message) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    public void SetTitle(string title) => TitleText.Text = title;
    public void SetMessage(string message) => MessageText.Text = message;

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var match = ConfirmInput.Text == ExpectedInput;
        ConfirmButton.IsEnabled = match;
        HintText.Text = match
            ? "✓ 已确认"
            : $"请输入：{ExpectedInput}";
        HintText.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["TextDimBrush"]
            ?? System.Windows.Media.Brushes.Gray;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = ConfirmInput.Text == ExpectedInput;
        if (Confirmed) DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}
