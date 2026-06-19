using GitX.Core.Models;
using GitX.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace GitX.Views;

public partial class MainWindow : ThemedWindow
{
    private MainWindowViewModel? _viewModel;
    private bool _diffHighlighterSetup;
    private readonly UnifiedDiffHighlighter _diffHighlighter;

    public MainWindow()
    {
        InitializeComponent();
        _diffHighlighter = new UnifiedDiffHighlighter(() => _viewModel?.UnifiedDiffRows ?? Array.Empty<GitX.Core.Models.UnifiedDiffRow>());
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(SetupDiffHighlighter, DispatcherPriority.Loaded);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncUnifiedDiffText();
            SyncPathHistoryRowHeight();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UnifiedDiffText))
        {
            Dispatcher.BeginInvoke(SyncUnifiedDiffText);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.UnifiedDiffRows))
        {
            Dispatcher.BeginInvoke(RefreshDiffHighlighting);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentDiffLineNumber))
        {
            Dispatcher.BeginInvoke(ScrollToCurrentDiffLine);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsPathHistoryCollapsed))
        {
            Dispatcher.BeginInvoke(SyncPathHistoryRowHeight);
        }
    }

    private void SyncPathHistoryRowHeight()
    {
        if (_viewModel == null || PathHistoryRow == null) return;
        PathHistoryRow.Height = _viewModel.IsPathHistoryCollapsed
            ? new GridLength(1, GridUnitType.Auto)
            : new GridLength(340, GridUnitType.Pixel);
    }

    private void SyncUnifiedDiffText()
    {
        if (DiffEditor == null || _viewModel == null) return;
        DiffEditor.Text = _viewModel.UnifiedDiffText ?? string.Empty;
        RefreshDiffHighlighting();
    }

    private void SetupDiffHighlighter()
    {
        if (_diffHighlighterSetup) return;
        if (DiffEditor?.TextArea?.TextView == null) return;

        DiffEditor.TextArea.TextView.LineTransformers.Add(_diffHighlighter);
        _diffHighlighterSetup = true;
        RefreshDiffHighlighting();
    }

    private void RefreshDiffHighlighting()
    {
        if (!_diffHighlighterSetup) return;
        DiffEditor.TextArea.TextView.Redraw();
    }

    private void ScrollToCurrentDiffLine()
    {
        if (_viewModel == null || DiffEditor == null) return;

        var line = _viewModel.CurrentDiffLineNumber;
        if (line <= 0) return;

        DiffEditor.ScrollToLine(line);
        if (DiffEditor.TextArea != null)
        {
            DiffEditor.TextArea.Caret.Line = line;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel == null) return;

        if (e.Key == Key.F3)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                _viewModel.PreviousDiffCommand.Execute(null);
            }
            else
            {
                _viewModel.NextDiffCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
        {
            DiffTreePaneControl.FocusFilterBox();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DiffTreePaneControl.IsKeyboardFocusWithin)
        {
            DiffTreePaneControl.FocusFilterBox();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        (_viewModel as IDisposable)?.Dispose();
    }

    private void OnCommitFileClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CommitFileChangeItem file) return;
        if (_viewModel.NavigateToCommitFileCommand.CanExecute(file))
        {
            _viewModel.NavigateToCommitFileCommand.Execute(file);
            // 跳转后短暂闪一下状态：成功高亮绿色，失败高亮黄色
            FlashBorder(fe, found: _viewModel.LastNavigationFound);
        }
    }

    private void FlashBorder(FrameworkElement fe, bool found)
    {
        if (fe is not System.Windows.Controls.Border border) return;
        var original = border.BorderBrush;
        var flashBrush = found
            ? (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["YellowBrush"];
        border.BorderBrush = flashBrush;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            border.BorderBrush = original;
        };
        timer.Start();
    }
}
