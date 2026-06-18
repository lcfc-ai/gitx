using GitX.ViewModels;
using System.Windows;
using System.Windows.Controls;
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

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel != null && e.NewValue is GitX.Core.Models.DiffTreeModel node)
        {
            _viewModel.SelectedTreeNode = node;
        }
    }

    private void OnDiffTreeItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item)
        {
            return;
        }

        if (item.DataContext is not GitX.Core.Models.DiffTreeModel node || node.IsFile || node.Children.Count == 0)
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
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
            TreeFilterBox.Focus();
            TreeFilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && TreeFilterBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(TreeFilterBox.Text))
        {
            TreeFilterBox.Clear();
            TreeFilterBox.Focus();
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

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        HandleChromeMouseDown(sender, e);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        MinimizeWindow();
    }

    private void OnToggleMaximizeClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseWindow();
    }
}
