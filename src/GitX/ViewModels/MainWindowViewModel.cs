using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitX.Core.Logging;
using GitX.Core.Models;
using GitX.Services;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;

namespace GitX.ViewModels;

/// <summary>
/// 主窗口 ViewModel
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly GitService _gitService;
    private readonly DiffService _diffService;
    private readonly DiffTreeFilterService _diffTreeFilterService;
    private readonly FileService _fileService;
    private readonly CacheLogService _cacheLog;
    private readonly TextDiffService _textDiffService;

    private readonly string _repoPath;
    private readonly string _currentBranch;
    private readonly string _targetBranch;
    private DiffTreeModel _diffRoot = new();
    private int _diffLoadVersion;
    private int _fileLoadVersion;

    [ObservableProperty]
    private ObservableCollection<DiffTreeModel> _diffTreeRoot = new();

    [ObservableProperty]
    private int _visibleChangeCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBranchDisplay))]
    [NotifyPropertyChangedFor(nameof(TargetBranchDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedNodeTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedNodeSubtitle))]
    private DiffTreeModel? _selectedTreeNode;

    [ObservableProperty]
    private string _treeFilterText = string.Empty;

    [ObservableProperty]
    private string _currentFileContent = string.Empty;

    [ObservableProperty]
    private string _targetFileContent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOverviewText))]
    private string _unifiedDiffText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOverviewText))]
    private IReadOnlyList<UnifiedDiffRow> _unifiedDiffRows = Array.Empty<UnifiedDiffRow>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOverviewText))]
    private bool _isBinaryFile = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDiffPositionText))]
    [NotifyPropertyChangedFor(nameof(CurrentDiffLineNumber))]
    [NotifyPropertyChangedFor(nameof(CanMoveToPreviousDiff))]
    [NotifyPropertyChangedFor(nameof(CanMoveToNextDiff))]
    [NotifyPropertyChangedFor(nameof(DiffOverviewText))]
    private int _currentDiffAnchorIndex = -1;

    [ObservableProperty]
    private string _statusBarText = "就绪";

    [ObservableProperty]
    private int _totalChangeCount = 0;

    [ObservableProperty]
    private WorkingTreeSummary? _workingTreeSummary;

    [ObservableProperty]
    private bool _isLoading = true;

    public string CurrentBranchDisplay => _currentBranch;
    public string TargetBranchDisplay => _targetBranch;
    public string SelectedNodeTitle => SelectedTreeNode?.FullPath ?? "点击左侧文件查看差异";
    public string SelectedNodeSubtitle => SelectedTreeNode == null
        ? string.Empty
        : SelectedTreeNode.IsFile
            ? $"{SelectedTreeNode.ChangeTypeDesc} · {SelectedTreeNode.ChangeTypeCode}"
            : $"本地目录统计 {SelectedTreeNode.FolderStatsSummary} · 共 {SelectedTreeNode.ChangeFileCount} 个文件";
    public string CurrentDiffPositionText
    {
        get
        {
            var anchors = GetDiffAnchorLineNumbers();
            if (anchors.Count == 0 || CurrentDiffAnchorIndex < 0)
            {
                return "0/0";
            }

            return $"{CurrentDiffAnchorIndex + 1}/{anchors.Count}";
        }
    }
    public int CurrentDiffLineNumber
    {
        get
        {
            var anchors = GetDiffAnchorLineNumbers();
            if (anchors.Count == 0 || CurrentDiffAnchorIndex < 0)
            {
                return 0;
            }

            return anchors[Math.Clamp(CurrentDiffAnchorIndex, 0, anchors.Count - 1)];
        }
    }
    public bool CanMoveToPreviousDiff => GetDiffAnchorLineNumbers().Count > 0 && CurrentDiffAnchorIndex > 0;
    public bool CanMoveToNextDiff => GetDiffAnchorLineNumbers().Count > 0 && CurrentDiffAnchorIndex >= 0 && CurrentDiffAnchorIndex < GetDiffAnchorLineNumbers().Count - 1;
    public string DiffOverviewText
    {
        get
        {
            if (IsBinaryFile) return "概览：二进制";
            if (UnifiedDiffRows.Count == 0) return "概览：当前无本地文本差异";

            var added = UnifiedDiffRows.Count(r => r.Kind == UnifiedDiffRowKind.Added);
            var deleted = UnifiedDiffRows.Count(r => r.Kind == UnifiedDiffRowKind.Deleted);
            var context = UnifiedDiffRows.Count(r => r.Kind == UnifiedDiffRowKind.Context);
            var inline = UnifiedDiffRows.Count(r => r.InlineSpans.Count > 0);
            return $"概览：+{added} · -{deleted} · {context} · 行内 {inline}";
        }
    }

    [RelayCommand]
    private void ClearTreeFilter()
    {
        TreeFilterText = string.Empty;
    }

    [RelayCommand]
    private void PreviousDiff()
    {
        MoveDiffAnchor(-1);
    }

    [RelayCommand]
    private void NextDiff()
    {
        MoveDiffAnchor(1);
    }

    public MainWindowViewModel(
        GitService gitService,
        DiffService diffService,
        DiffTreeFilterService diffTreeFilterService,
        FileService fileService,
        CacheLogService cacheLog,
        TextDiffService textDiffService,
        string repoPath,
        string currentBranch,
        string targetBranch)
    {
        _gitService = gitService;
        _diffService = diffService;
        _diffTreeFilterService = diffTreeFilterService;
        _fileService = fileService;
        _cacheLog = cacheLog;
        _textDiffService = textDiffService;
        _repoPath = repoPath;
        _currentBranch = currentBranch;
        _targetBranch = targetBranch;

        _cacheLog.LogBranchCompare(repoPath, currentBranch, targetBranch);
        _ = LoadDiffAsync();
    }

    partial void OnSelectedTreeNodeChanged(DiffTreeModel? value)
    {
        if (value?.IsFile == true)
        {
            _ = LoadFileDiffAsync(value.FullPath);
            return;
        }

        Interlocked.Increment(ref _fileLoadVersion);
        ClearFileDiffDisplay();
        CurrentDiffAnchorIndex = -1;
        StatusBarText = value == null
            ? "未选择文件"
            : $"已选择目录: {value.FullPath}";
    }

    partial void OnTreeFilterTextChanged(string value)
    {
        ApplyTreeFilter(SelectedTreeNode?.FullPath);
    }

    partial void OnUnifiedDiffRowsChanged(IReadOnlyList<UnifiedDiffRow> value)
    {
        ResetDiffAnchor();
    }

    private async Task LoadDiffAsync()
    {
        var loadVersion = Interlocked.Increment(ref _diffLoadVersion);

        try
        {
            IsLoading = true;
            StatusBarText = "正在计算分支差异...";

            int count = 0;
            WorkingTreeSummary? summary = null;
            // 差异计算与文件路径遍历都在后台线程完成，避免大仓库卡 UI 线程
            // BuildDiffTree 对比「本地工作区」vs「目标分支 commit」，只显示真正不一致的文件
            await Task.Run(() =>
            {
                _diffRoot = _diffService.BuildDiffTree(_currentBranch, _targetBranch);
                count = _diffRoot.ChangeFileCount;
                summary = _gitService.GetWorkingTreeSummary(_currentBranch, _targetBranch);
            });

            if (loadVersion != Volatile.Read(ref _diffLoadVersion))
            {
                return;
            }

            DiffTreeRoot = new ObservableCollection<DiffTreeModel>(_diffRoot.Children);
            TotalChangeCount = count;
            VisibleChangeCount = count;
            WorkingTreeSummary = summary;
            StatusBarText = "差异已加载";
            ApplyTreeFilter(SelectedTreeNode?.FullPath);
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _diffLoadVersion))
            {
                return;
            }

            AppLog.Error(ex, "加载差异失败");
            StatusBarText = $"加载差异失败: {ex.Message}";
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _diffLoadVersion))
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadFileDiffAsync(string filePath)
    {
        var loadVersion = Interlocked.Increment(ref _fileLoadVersion);

        try
        {
            StatusBarText = $"加载文件: {filePath}";

            var snapshot = await Task.Run(() =>
            {
                // 目标分支内容（带缓存）
                var targetCacheKey = $"{_targetBranch}:{filePath}";
                var targetContent = _cacheLog.GetCachedBlob(targetCacheKey);
                if (targetContent == null)
                {
                    var (content, isBinary) = _gitService.GetFileContent(_targetBranch, filePath);
                    if (isBinary)
                    {
                        return (
                            isBinary: true,
                            unifiedText: "[二进制文件，无法对比文本]",
                            unifiedRows: Array.Empty<UnifiedDiffRow>());
                    }

                    targetContent = content;
                    _cacheLog.CacheBlob(targetCacheKey, targetContent);
                }

                // 本地工作区内容（与差异树口径一致：直接读磁盘）
                var currentCacheKey = $"worktree:{filePath}";
                var currentContent = _cacheLog.GetCachedBlob(currentCacheKey);
                if (currentContent == null)
                {
                    var (content, isBinary) = _gitService.GetWorkingFileContent(filePath);
                    if (isBinary)
                    {
                        return (
                            isBinary: true,
                            unifiedText: "[二进制文件，无法对比文本]",
                            unifiedRows: Array.Empty<UnifiedDiffRow>());
                    }

                    currentContent = content;
                    _cacheLog.CacheBlob(currentCacheKey, currentContent);
                }

                var diff = _textDiffService.BuildUnifiedDiff(currentContent ?? string.Empty, targetContent ?? string.Empty);

                return (
                    isBinary: false,
                    unifiedText: diff.Text,
                    unifiedRows: diff.Rows);
            });

            if (loadVersion != Volatile.Read(ref _fileLoadVersion))
            {
                return;
            }

            IsBinaryFile = snapshot.isBinary;
            UnifiedDiffText = snapshot.unifiedText;
            UnifiedDiffRows = snapshot.unifiedRows;
            StatusBarText = snapshot.isBinary ? $"本地文件: {filePath}（二进制）" : $"本地文件: {filePath}";
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _fileLoadVersion))
            {
                return;
            }

            IsBinaryFile = false;
            UnifiedDiffText = string.Empty;
            UnifiedDiffRows = Array.Empty<UnifiedDiffRow>();
            AppLog.Error(ex, "加载文件差异失败: {File}", filePath);
            StatusBarText = $"加载失败: {filePath}";
        }
    }

    private void ClearFileDiffDisplay()
    {
        IsBinaryFile = false;
        CurrentFileContent = string.Empty;
        TargetFileContent = string.Empty;
        UnifiedDiffText = string.Empty;
        UnifiedDiffRows = Array.Empty<UnifiedDiffRow>();
    }

    private void ResetDiffAnchor()
    {
        var anchors = GetDiffAnchorLineNumbers();
        CurrentDiffAnchorIndex = anchors.Count > 0 ? 0 : -1;
    }

    private void MoveDiffAnchor(int delta)
    {
        var anchors = GetDiffAnchorLineNumbers();
        if (anchors.Count == 0)
        {
            CurrentDiffAnchorIndex = -1;
            return;
        }

        var next = CurrentDiffAnchorIndex < 0 ? 0 : CurrentDiffAnchorIndex + delta;
        CurrentDiffAnchorIndex = Math.Clamp(next, 0, anchors.Count - 1);
    }

    private void ApplyTreeFilter(string? preservePath = null)
    {
        var filtered = _diffTreeFilterService.Filter(_diffRoot.Children, TreeFilterText);
        DiffTreeRoot = filtered;
        VisibleChangeCount = filtered.Sum(node => node.ChangeFileCount);

        if (string.IsNullOrWhiteSpace(preservePath))
        {
            return;
        }

        var selected = FindNodeByFullPath(filtered, preservePath);
        if (!ReferenceEquals(selected, SelectedTreeNode))
        {
            SelectedTreeNode = selected;
        }
    }

    private static DiffTreeModel? FindNodeByFullPath(IEnumerable<DiffTreeModel> nodes, string fullPath)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindNodeByFullPath(node.Children, fullPath);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private IReadOnlyList<int> GetDiffAnchorLineNumbers()
    {
        if (UnifiedDiffRows.Count == 0)
        {
            return Array.Empty<int>();
        }

        return UnifiedDiffRows
            .Select((row, index) => (row, index))
            .Where(x => x.row.Kind != UnifiedDiffRowKind.Context)
            .Select(x => x.index + 1)
            .ToArray();
    }

    [RelayCommand]
    private async Task OverwriteSelectedFileAsync()
    {
        if (SelectedTreeNode?.IsFile != true) return;
        var filePath = SelectedTreeNode.FullPath;

        var result = MessageBox.Show(
            $"确认下载并覆盖本地文件？\n\n文件: {filePath}\n来源: {_targetBranch}\n\n此操作将覆盖您本地的文件，请确认。",
            "GitX 确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        StatusBarText = "正在覆盖...";
        var (ok, err) = await Task.Run(() => _fileService.OverwriteFile(filePath, _targetBranch, _currentBranch));
        if (ok)
        {
            _cacheLog.RemoveCachedBlob($"worktree:{filePath}");
        }
        StatusBarText = ok ? $"已覆盖: {filePath}" : $"覆盖失败: {err}";
        await LoadDiffAsync();
    }

    [RelayCommand]
    private async Task OverwriteSelectedFolderAsync()
    {
        if (SelectedTreeNode?.IsFile == true || SelectedTreeNode == null) return;

        var allFiles = _fileService.GetAllFilePaths(SelectedTreeNode);
        var result = MessageBox.Show(
            $"确认下载目录 \"{SelectedTreeNode.Name}\" 下所有差异文件？\n\n共 {allFiles.Count} 个文件，来源: {_targetBranch}\n\n此操作将覆盖本地对应文件，请确认。",
            "GitX 确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        StatusBarText = "正在批量覆盖...";
        var (success, fail, errors) = await Task.Run(() => _fileService.OverwriteFolder(SelectedTreeNode, _targetBranch, _currentBranch));

        if (errors.Count > 0)
        {
            AppLog.Warn("批量覆盖部分失败: {Errors}", string.Join(";", errors));
        }

        _cacheLog.ClearWorkingTreeBlobCache();
        StatusBarText = $"批量覆盖完成: {success} 成功, {fail} 失败";
        await LoadDiffAsync();
    }

    [RelayCommand]
    private async Task OverwriteAllFilesAsync()
    {
        var result = MessageBox.Show(
        $"确认下载全部 {TotalChangeCount} 个本地差异文件？\n\n来源: {_targetBranch}\n目标: {_currentBranch} 本地工作区\n\n此操作将覆盖所有本地差异文件。",
            "GitX 确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        StatusBarText = "正在全部覆盖...";
        // 复用 OverwriteFolder 的统一实现，避免重复代码并收集错误详情；遍历在后台线程完成
        var (success, fail, errors) = await Task.Run(() => _fileService.OverwriteFolder(_diffRoot, _targetBranch, _currentBranch));

        if (errors.Count > 0)
        {
            AppLog.Warn("全部覆盖部分失败: {Errors}", string.Join(";", errors));
        }

        _cacheLog.ClearWorkingTreeBlobCache();
        StatusBarText = $"全部覆盖完成: {success} 成功, {fail} 失败";
        await LoadDiffAsync();
    }

    [RelayCommand]
    private async Task RefreshDiffAsync()
    {
        // 刷新先重新 Fetch 远程，否则只能看到本地已有的提交，看不到远端新提交
        StatusBarText = "正在同步远程分支...";
        await Task.Run(() => _gitService.FetchRemote());
        await LoadDiffAsync();
    }

    public void Dispose()
    {
        _cacheLog?.Dispose();
        _gitService?.Dispose();
    }
}
