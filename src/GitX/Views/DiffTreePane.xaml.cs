using GitX.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitX.Views;

public partial class DiffTreePane : UserControl
{
    public DiffTreePane()
    {
        InitializeComponent();
    }

    public void FocusFilterBox()
    {
        TreeFilterBox.Focus();
        TreeFilterBox.SelectAll();
    }

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel viewModel && e.NewValue is GitX.Core.Models.DiffTreeModel node)
        {
            viewModel.SelectedTreeNode = node;
        }
    }

    private void OnDiffTreeItemRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = sender as TreeViewItem
            ?? (sender as FrameworkElement)?.TemplatedParent as TreeViewItem;

        if (item?.DataContext is not GitX.Core.Models.DiffTreeModel node)
        {
            return;
        }

        if (node.IsFile || node.Children.Count == 0)
        {
            item.IsSelected = true;
            if (DataContext is MainWindowViewModel viewModel && !ReferenceEquals(viewModel.SelectedTreeNode, node))
            {
                viewModel.SelectedTreeNode = node;
            }

            return;
        }

        item.IsSelected = true;
        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }
}
