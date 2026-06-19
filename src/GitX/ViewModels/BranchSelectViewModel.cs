using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitX.Core.Logging;
using GitX.Core.Models;
using GitX.Services;
using System.Collections.ObjectModel;

namespace GitX.ViewModels;

/// <summary>
/// 分支选择窗口 ViewModel
/// </summary>
public partial class BranchSelectViewModel : ObservableObject
{
    private readonly GitService _gitService;

    [ObservableProperty]
    private ObservableCollection<BranchModel> _branches = new();

    [ObservableProperty]
    private ObservableCollection<BranchModel> _filteredBranches = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private BranchModel? _selectedBranch;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    public BranchSelectViewModel(GitService gitService)
    {
        _gitService = gitService;
        _ = LoadBranchesAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterBranches(value);
    }

    private async Task LoadBranchesAsync()
    {
        try
        {
            IsLoading = true;
            // 打开分支选择窗口时不自动 fetch，避免用户没准备好就发起网络请求
            // 远端最新需要时由用户在主窗口点「拉取最新」按钮触发
            var all = _gitService.GetAllBranches();
            Branches = new ObservableCollection<BranchModel>(all);
            FilteredBranches = new ObservableCollection<BranchModel>(all);
            AppLog.Info("加载分支 {Count} 个", all.Count);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "加载分支失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FilterBranches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            FilteredBranches = new ObservableCollection<BranchModel>(Branches);
            return;
        }

        var lower = keyword.ToLowerInvariant();
        var filtered = Branches.Where(b => b.DisplayName.ToLowerInvariant().Contains(lower)).ToList();
        FilteredBranches = new ObservableCollection<BranchModel>(filtered);
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (SelectedBranch == null) return;
        AppLog.Info("用户选择分支: {Branch}", SelectedBranch.DisplayName);
    }

    [RelayCommand]
    private void Cancel()
    {
        SelectedBranch = null;
    }

    private bool CanConfirm => SelectedBranch != null && !SelectedBranch.IsCurrent;
}
