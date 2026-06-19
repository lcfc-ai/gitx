using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitX.Core.Logging;
using GitX.Core.Models;
using GitX.Services;
using System.Collections.ObjectModel;
using System.IO;
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
    // 提交详情 LRU 缓存：同 SHA 重复点击立即返回，避免反复 fork git 子进程
    private const int MaxCommitDetailCacheEntries = 200;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CommitDetailResult> _commitDetailCache = new();
    private readonly TextDiffService _textDiffService;

    private readonly string _repoPath;
    private readonly string _currentBranch;
    private readonly string _targetBranch;
    private DiffTreeModel _diffRoot = new();
    private int _diffLoadVersion;
    private int _fileLoadVersion;
    private int _pathHistoryLoadVersion;
    private int _commitDetailLoadVersion;
    private IReadOnlyList<int> _cachedDiffAnchors = Array.Empty<int>();

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathHistoryItems))]
    private ObservableCollection<CommitHistoryItem> _pathHistoryItems = new();

    [ObservableProperty]
    private bool _isPathHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCommitTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitAuthorEmail))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitRelativeTime))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitParentShas))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitFullSha))]
    private CommitHistoryItem? _selectedPathHistoryItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCommitTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitAuthorEmail))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitRelativeTime))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitParentShas))]
    [NotifyPropertyChangedFor(nameof(SelectedCommitFullSha))]
    private CommitDetailResult? _selectedCommitDetail;

    [ObservableProperty]
    private bool _isCommitDetailLoading;

    [ObservableProperty]
    private string _pathHistoryTitle = "提交记录";

    [ObservableProperty]
    private string _pathHistorySubtitle = "选择文件或文件夹查看目标分支的提交记录";

    // 最近一次「跳转到提交中文件」的结果：true=已定位，false=未在差异列表中找到
    // 给视图层做短暂高亮反馈用
    public bool LastNavigationFound { get; private set; } = true;

    [ObservableProperty]
    private string _pathHistoryAuthorFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathHistoryRecentDaysLabel))]
    private int _pathHistoryRecentDaysIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathHistoryCollapseIcon))]
    [NotifyPropertyChangedFor(nameof(CanExpandPathHistory))]
    private bool _isPathHistoryCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBranchDisplay))]
    [NotifyPropertyChangedFor(nameof(TargetBranchDisplay))]
    private bool _isFetchingRemote;

    [ObservableProperty]
    private string _fetchProgressText = string.Empty;

    // 当前对比基线：fetch 远端后会升级为 origin/xxx；切回本地分支时再退回去
    private string _effectiveCurrentBranch => _gitService.GetRemoteTrackingBranch(_currentBranch) ?? _currentBranch;
    private string _effectiveTargetBranch => _gitService.GetRemoteTrackingBranch(_targetBranch) ?? _targetBranch;

    public IReadOnlyList<string> PathHistoryRecentDaysOptions { get; } = new[] { "全部", "7 天", "30 天", "90 天" };

    public bool HasPathHistoryItems => PathHistoryItems.Count > 0;
    public string PathHistoryRecentDaysLabel => PathHistoryRecentDaysOptions[Math.Clamp(PathHistoryRecentDaysIndex, 0, PathHistoryRecentDaysOptions.Count - 1)];
    public string PathHistoryCollapseIcon => IsPathHistoryCollapsed ? "▴" : "▾";
    public bool CanExpandPathHistory => IsPathHistoryCollapsed;
    public string SelectedCommitTitle => SelectedPathHistoryItem == null
        ? "点击一条提交查看详情"
        : $"{SelectedPathHistoryItem.ShortSha} · {SelectedPathHistoryItem.Author}";

    public string SelectedCommitAuthorEmail => SelectedPathHistoryItem?.AuthorEmail ?? string.Empty;
    public string SelectedCommitRelativeTime => SelectedPathHistoryItem?.CommitTimeRelative ?? string.Empty;
    public string SelectedCommitParentShas => SelectedPathHistoryItem?.ParentShasText ?? string.Empty;
    public string SelectedCommitFullSha => SelectedPathHistoryItem?.Sha ?? string.Empty;

    public string CurrentBranchDisplay
    {
        get
        {
            var remote = _gitService.GetRemoteTrackingBranch(_currentBranch);
            return remote == null ? _currentBranch : $"{_currentBranch} → {remote}";
        }
    }
    public string TargetBranchDisplay
    {
        get
        {
            var remote = _gitService.GetRemoteTrackingBranch(_targetBranch);
            return remote == null ? _targetBranch : $"{_targetBranch} → {remote}";
        }
    }
    public string SelectedNodeTitle => SelectedTreeNode?.FullPath ?? "点击左侧文件查看差异";
    public string SelectedNodeSubtitle => SelectedTreeNode == null
        ? string.Empty
        : SelectedTreeNode.IsFile && SelectedTreeNode.ChangeType == LibGit2Sharp.ChangeKind.Renamed && !string.IsNullOrWhiteSpace(SelectedTreeNode.OldPath)
            ? $"重命名 · {SelectedTreeNode.OldPath} -> {SelectedTreeNode.FullPath}"
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
            _ = LoadFileDiffAsync(value);
        }

        if (value?.IsFile != true)
        {
            Interlocked.Increment(ref _fileLoadVersion);
            ClearFileDiffDisplay();
            CurrentDiffAnchorIndex = -1;
            StatusBarText = value == null
                ? "未选择文件"
                : $"已选择目录: {value.FullPath}";
        }

        _ = LoadPathHistoryAsync(value);
    }

    private int _filterDebounceVersion;

    partial void OnTreeFilterTextChanged(string value)
    {
        // 250ms 防抖：避免连续按键时每字都重建整棵 ObservableCollection
        var myVersion = Interlocked.Increment(ref _filterDebounceVersion);
        _ = DebouncedFilterAsync(myVersion);
    }

    private async Task DebouncedFilterAsync(int myVersion)
    {
        await Task.Delay(250);
        if (myVersion != Volatile.Read(ref _filterDebounceVersion)) return;
        ApplyTreeFilter(SelectedTreeNode?.FullPath);
    }

    partial void OnPathHistoryAuthorFilterChanged(string value)
    {
        _ = ReloadPathHistoryAsync();
    }

    partial void OnPathHistoryRecentDaysIndexChanged(int value)
    {
        _ = ReloadPathHistoryAsync();
    }

    partial void OnSelectedPathHistoryItemChanged(CommitHistoryItem? value)
    {
        _ = LoadSelectedCommitDetailAsync(value);
    }

    partial void OnUnifiedDiffRowsChanged(IReadOnlyList<UnifiedDiffRow> value)
    {
        RecomputeDiffAnchors();
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
            var effectiveCurrent = _effectiveCurrentBranch;
            var effectiveTarget = _effectiveTargetBranch;
            // 差异计算与文件路径遍历都在后台线程完成，避免大仓库卡 UI 线程
            // BuildDiffTree 对比「本地工作区」vs「目标分支 commit」，只显示真正不一致的文件
            // 当前分支改用 origin/xxx 作为「远端最新」基线，fetch 后会自动对齐
            // 一次调用同时返回 summary：避免上层再 GetTreeChanges 一次，省一半 git diff 耗时
            await Task.Run(() =>
            {
                var (root, sum) = _diffService.BuildDiffTreeWithSummary(effectiveCurrent, effectiveTarget);
                _diffRoot = root;
                count = root.ChangeFileCount;
                summary = sum;
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

    private async Task LoadFileDiffAsync(DiffTreeModel fileNode)
    {
        var filePath = fileNode.FullPath;
        var effectiveTarget = _effectiveTargetBranch;

        // 缓存命中：UI 线程同步赋值，跳过 Task.Run 和版本号竞争
        var targetCacheKey = $"{effectiveTarget}:{filePath}";
        var currentCacheKey = $"worktree:{filePath}";
        var cachedTarget = _cacheLog.GetCachedBlob(targetCacheKey);
        var cachedCurrent = _cacheLog.GetCachedBlob(currentCacheKey);
        if (cachedTarget != null && cachedCurrent != null)
        {
            IsBinaryFile = false;
            var (unifiedText, unifiedRows) = await Task.Run(() =>
            {
                var diff = _textDiffService.BuildUnifiedDiff(cachedCurrent, cachedTarget);
                return (diff.Text, diff.Rows);
            });
            if (UnifiedDiffText == unifiedText && ReferenceEquals(UnifiedDiffRows, unifiedRows))
            {
                return;
            }
            UnifiedDiffText = unifiedText;
            UnifiedDiffRows = unifiedRows;
            StatusBarText = $"本地文件: {filePath}";
            return;
        }

        var loadVersion = Interlocked.Increment(ref _fileLoadVersion);

        try
        {
            StatusBarText = $"加载文件: {filePath}";

            var snapshot = await Task.Run(() =>
            {
                // 目标分支内容（带缓存），缓存 key 用远端跟踪分支以匹配新的对比基线
                var targetContent = _cacheLog.GetCachedBlob(targetCacheKey);
                if (targetContent == null)
                {
                    var (content, isBinary) = _gitService.GetFileContent(effectiveTarget, filePath);
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
                var localPath = filePath;
                var localFullPath = Path.Combine(_repoPath, filePath);
                if (!File.Exists(localFullPath) &&
                    fileNode.ChangeType == LibGit2Sharp.ChangeKind.Renamed &&
                    !string.IsNullOrWhiteSpace(fileNode.OldPath))
                {
                    localPath = fileNode.OldPath;
                }

                var currentKey = $"worktree:{localPath}";
                var currentContent = _cacheLog.GetCachedBlob(currentKey);
                if (currentContent == null)
                {
                    var (content, isBinary) = _gitService.GetWorkingFileContent(localPath);
                    if (isBinary)
                    {
                        return (
                            isBinary: true,
                            unifiedText: "[二进制文件，无法对比文本]",
                            unifiedRows: Array.Empty<UnifiedDiffRow>());
                    }

                    currentContent = content;
                    _cacheLog.CacheBlob(currentKey, currentContent);
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
            if (fileNode.ChangeType == LibGit2Sharp.ChangeKind.Renamed &&
                !string.IsNullOrWhiteSpace(fileNode.OldPath) &&
                !string.Equals(fileNode.OldPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                StatusBarText = snapshot.isBinary
                    ? $"本地文件: {fileNode.OldPath} -> {filePath}（二进制）"
                    : $"本地文件: {fileNode.OldPath} -> {filePath}";
            }
            else
            {
                StatusBarText = snapshot.isBinary ? $"本地文件: {filePath}（二进制）" : $"本地文件: {filePath}";
            }
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

    private void ClearPathHistoryDisplay()
    {
        PathHistoryTitle = "提交记录";
        PathHistorySubtitle = "选择文件或文件夹查看目标分支的提交记录";
        PathHistoryItems = new ObservableCollection<CommitHistoryItem>();
        SelectedPathHistoryItem = null;
        SelectedCommitDetail = null;
        IsCommitDetailLoading = false;
    }

    private async Task ReloadPathHistoryAsync()
    {
        await LoadPathHistoryAsync(SelectedTreeNode, preserveSelection: true);
    }

    private int? GetRecentDaysFilter()
    {
        return PathHistoryRecentDaysIndex switch
        {
            1 => 7,
            2 => 30,
            3 => 90,
            _ => null
        };
    }

    private async Task LoadPathHistoryAsync(DiffTreeModel? node, bool preserveSelection = false)
    {
        var loadVersion = Interlocked.Increment(ref _pathHistoryLoadVersion);

        if (node == null)
        {
            ClearPathHistoryDisplay();
            IsPathHistoryLoading = false;
            return;
        }

        var selectedPath = node.FullPath;
        PathHistoryTitle = node.IsFile ? "文件提交记录" : "文件夹提交记录";
        PathHistorySubtitle = node.IsFile
            ? selectedPath
            : $"{selectedPath} · 显示该目录下相关提交";
        IsPathHistoryLoading = true;

        try
        {
            var authorFilter = PathHistoryAuthorFilter.Trim();
            var recentDays = GetRecentDaysFilter();
            var effectiveTarget = _effectiveTargetBranch;
            var items = await Task.Run(() => _gitService.GetPathHistory(effectiveTarget, selectedPath, node.IsFile, 50, authorFilter, recentDays));

            if (loadVersion != Volatile.Read(ref _pathHistoryLoadVersion))
            {
                return;
            }

            PathHistoryItems = new ObservableCollection<CommitHistoryItem>(items);
            var preferredSha = preserveSelection ? SelectedPathHistoryItem?.Sha : null;
            SelectedPathHistoryItem = items.FirstOrDefault(x => string.Equals(x.Sha, preferredSha, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault();
            if (items.Count == 0)
            {
                PathHistorySubtitle = node.IsFile
                    ? $"{selectedPath} · 目标分支暂无提交记录"
                    : $"{selectedPath} · 目标分支暂无相关提交记录";
                SelectedCommitDetail = null;
            }
            else if (!string.IsNullOrWhiteSpace(authorFilter) || recentDays.HasValue)
            {
                var filterParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(authorFilter))
                {
                    filterParts.Add($"作者: {authorFilter}");
                }
                if (recentDays.HasValue)
                {
                    filterParts.Add($"最近 {recentDays.Value} 天");
                }
                PathHistorySubtitle = $"{selectedPath} · {string.Join(" · ", filterParts)}";
            }
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _pathHistoryLoadVersion))
            {
                return;
            }

            AppLog.Warn(ex, "加载路径提交记录失败: {Path}", selectedPath);
            PathHistoryItems = new ObservableCollection<CommitHistoryItem>();
            SelectedPathHistoryItem = null;
            SelectedCommitDetail = null;
            PathHistorySubtitle = $"{selectedPath} · 提交记录加载失败";
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _pathHistoryLoadVersion))
            {
                IsPathHistoryLoading = false;
            }
        }
    }

    private async Task LoadSelectedCommitDetailAsync(CommitHistoryItem? item)
    {
        var loadVersion = Interlocked.Increment(ref _commitDetailLoadVersion);
        if (item == null)
        {
            SelectedCommitDetail = null;
            IsCommitDetailLoading = false;
            return;
        }

        // 命中缓存：直接复用，不显示 loading，不重新加载
        if (_commitDetailCache.TryGetValue(item.Sha, out var cached))
        {
            SelectedCommitDetail = cached;
            IsCommitDetailLoading = false;
            return;
        }

        IsCommitDetailLoading = true;
        try
        {
            var detail = await Task.Run(() => _gitService.GetCommitDetail(item.Sha));
            if (loadVersion != Volatile.Read(ref _commitDetailLoadVersion))
            {
                return;
            }

            if (detail != null)
            {
                // 超过容量上限时直接清空，避免长时间浏览吃光内存
                if (_commitDetailCache.Count >= MaxCommitDetailCacheEntries)
                {
                    _commitDetailCache.Clear();
                }
                _commitDetailCache.TryAdd(item.Sha, detail);
            }
            SelectedCommitDetail = detail;
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _commitDetailLoadVersion))
            {
                return;
            }

            AppLog.Warn(ex, "加载提交详情失败: {Sha}", item.Sha);
            SelectedCommitDetail = null;
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _commitDetailLoadVersion))
            {
                IsCommitDetailLoading = false;
            }
        }
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
        var (filtered, visibleCount) = _diffTreeFilterService.FilterWithCount(_diffRoot.Children, TreeFilterText);
        DiffTreeRoot = filtered;
        VisibleChangeCount = visibleCount;

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

    private void RecomputeDiffAnchors()
    {
        if (UnifiedDiffRows.Count == 0)
        {
            _cachedDiffAnchors = Array.Empty<int>();
            return;
        }

        _cachedDiffAnchors = UnifiedDiffRows
            .Select((row, index) => (row, index))
            .Where(x => x.row.Kind != UnifiedDiffRowKind.Context)
            .Select(x => x.index + 1)
            .ToArray();
    }

    private IReadOnlyList<int> GetDiffAnchorLineNumbers()
    {
        return _cachedDiffAnchors;
    }

    [RelayCommand]
    private async Task OverwriteSelectedFileAsync()
    {
        if (SelectedTreeNode?.IsFile != true) return;
        var fileNode = SelectedTreeNode;
        var filePath = fileNode.FullPath;
        var fileName = fileNode.Name;
        var effectiveTarget = _effectiveTargetBranch;

        var confirm = new Views.ConfirmDangerDialog(
            "覆盖此文件",
            $"将下载并覆盖本地文件：\n{filePath}\n来源: {effectiveTarget}\n\n此操作不可撤销，请输入文件名「{fileName}」以确认：")
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            ExpectedInput = fileName
        };
        if (confirm.ShowDialog() != true || !confirm.Confirmed)
        {
            StatusBarText = "已取消覆盖";
            return;
        }

        StatusBarText = "正在覆盖...";
        var (ok, err) = await Task.Run(() => _fileService.OverwriteFile(filePath, effectiveTarget, _currentBranch, fileNode));
        if (ok)
        {
            _cacheLog.ClearWorkingTreeBlobCache();
        }
        StatusBarText = ok ? $"已覆盖: {filePath}" : $"覆盖失败: {err}";
        await LoadDiffAsync();
        await LoadPathHistoryAsync(SelectedTreeNode);
    }

    [RelayCommand]
    private async Task OverwriteSelectedFolderAsync()
    {
        if (SelectedTreeNode?.IsFile == true || SelectedTreeNode == null) return;

        var folderName = SelectedTreeNode.Name;
        var allFiles = _fileService.GetAllFilePaths(SelectedTreeNode);
        var effectiveTarget = _effectiveTargetBranch;
        var confirm = new Views.ConfirmDangerDialog(
            "覆盖整个文件夹",
            $"将下载并覆盖文件夹「{folderName}」下 {allFiles.Count} 个文件。\n来源分支: {effectiveTarget}\n此操作不可撤销。\n\n请输入文件夹名「{folderName}」以确认继续：")
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            ExpectedInput = folderName
        };
        if (confirm.ShowDialog() != true || !confirm.Confirmed)
        {
            StatusBarText = "已取消批量覆盖";
            return;
        }

        StatusBarText = "正在批量覆盖...";
        var (success, fail, errors) = await Task.Run(() => _fileService.OverwriteFolder(SelectedTreeNode, effectiveTarget, _currentBranch));

        if (errors.Count > 0)
        {
            AppLog.Warn("批量覆盖部分失败: {Errors}", string.Join(";", errors));
        }

        _cacheLog.ClearWorkingTreeBlobCache();
        StatusBarText = $"批量覆盖完成: {success} 成功, {fail} 失败";
        await LoadDiffAsync();
        await LoadPathHistoryAsync(SelectedTreeNode);
    }

    [RelayCommand]
    private async Task OverwriteAllFilesAsync()
    {
        var effectiveTarget = _effectiveTargetBranch;
        var result = MessageBox.Show(
        $"确认下载全部 {TotalChangeCount} 个本地差异文件？\n\n来源: {effectiveTarget}\n目标: {_currentBranch} 本地工作区\n\n此操作将覆盖所有本地差异文件。",
            "GitX 确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        StatusBarText = "正在全部覆盖...";
        // 复用 OverwriteFolder 的统一实现，避免重复代码并收集错误详情；遍历在后台线程完成
        var (success, fail, errors) = await Task.Run(() => _fileService.OverwriteFolder(_diffRoot, effectiveTarget, _currentBranch));

        if (errors.Count > 0)
        {
            AppLog.Warn("全部覆盖部分失败: {Errors}", string.Join(";", errors));
        }

        _cacheLog.ClearWorkingTreeBlobCache();
        StatusBarText = $"全部覆盖完成: {success} 成功, {fail} 失败";
        await LoadDiffAsync();
        await LoadPathHistoryAsync(SelectedTreeNode);
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedTreeNode == null) return;
        try
        {
            System.Windows.Clipboard.SetText(SelectedTreeNode.FullPath);
            StatusBarText = $"已复制路径: {SelectedTreeNode.FullPath}";
        }
        catch (Exception ex)
        {
            StatusBarText = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (SelectedTreeNode == null) return;
        try
        {
            var path = SelectedTreeNode.FullPath;
            if (SelectedTreeNode.IsFile)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            StatusBarText = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        if (SelectedTreeNode == null || SelectedTreeNode.IsFile) return;
        var target = SelectedTreeNode.IsExpanded;
        foreach (var node in EnumerateAllDescendants(SelectedTreeNode))
        {
            if (!node.IsFile) node.IsExpanded = !target;
        }
        SelectedTreeNode.IsExpanded = !target;
    }

    private static IEnumerable<DiffTreeModel> EnumerateAllDescendants(DiffTreeModel root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var d in EnumerateAllDescendants(child)) yield return d;
        }
    }

    [RelayCommand]
    private void NavigateToCommitFile(CommitFileChangeItem? file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.Path)) return;
        // 找到对应树节点并选中（当前根 + 所有展开的子节点）
        var node = DiffTreeRoot == null
            ? null
            : _diffTreeFilterService.FindNodeByPath(DiffTreeRoot, file.Path);
        if (node != null)
        {
            SelectedTreeNode = node;
            LastNavigationFound = true;
            StatusBarText = $"已跳转到: {file.Path}";
        }
        else
        {
            LastNavigationFound = false;
            StatusBarText = $"未在差异列表中找到: {file.Path}";
        }
    }

    [RelayCommand]
    private void CopyShaToClipboard(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return;
        try
        {
            System.Windows.Clipboard.SetText(sha);
            StatusBarText = $"已复制 SHA: {sha[..Math.Min(8, sha.Length)]}";
        }
        catch
        {
            StatusBarText = "复制失败，剪贴板被占用";
        }
    }

    [RelayCommand]
    private void TogglePathHistoryCollapse()
    {
        IsPathHistoryCollapsed = !IsPathHistoryCollapsed;
    }

    [RelayCommand]
    private void CopyFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            System.Windows.Clipboard.SetText(path);
            StatusBarText = $"已复制路径: {path}";
        }
        catch (Exception ex)
        {
            StatusBarText = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFileInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_repoPath, path);
            if (File.Exists(fullPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            }
            else
            {
                // 文件可能存在于目标分支但本地工作区没有，打开所在目录
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch (Exception ex)
        {
            StatusBarText = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDiffAsync()
    {
        // 刷新只重算本地差异，不再自动 fetch。远端拉取由用户主动点「拉取最新」按钮触发。
        await LoadDiffAsync();
        await LoadPathHistoryAsync(SelectedTreeNode);
    }

    [RelayCommand]
    private async Task FetchLatestAsync()
    {
        // 独立「拉取最新」按钮：仅同步远端 refs 并刷新对比基线，不动 working tree、不动本地分支指针
        if (IsFetchingRemote) return;
        IsFetchingRemote = true;
        try
        {
            await RunFetchAsync(silent: false);
            // fetch 之后 origin/xxx 可能变了：清掉依赖远端的缓存，重新加载
            _commitDetailCache.Clear();
            _cacheLog.ClearBlobCache();
            await LoadDiffAsync();
            await LoadPathHistoryAsync(SelectedTreeNode);
            StatusBarText = $"拉取完成，对比基线: {_effectiveCurrentBranch} vs {_effectiveTargetBranch}";
        }
        finally
        {
            IsFetchingRemote = false;
            FetchProgressText = string.Empty;
        }
    }

    private async Task RunFetchAsync(bool silent)
    {
        // 进度回调在子线程，必须切回 UI 线程更新 FetchProgressText
        var scheduler = SynchronizationContext.Current ?? new SynchronizationContext();
        void Report(string line) => scheduler.Post(_ => FetchProgressText = line, null);

        if (!silent)
        {
            StatusBarText = "正在同步远程分支...";
            FetchProgressText = "准备 Fetch...";
        }

        await Task.Run(() => _gitService.FetchRemoteWithProgress(Report));

        if (!silent)
        {
            FetchProgressText = string.IsNullOrEmpty(FetchProgressText) || FetchProgressText == "准备 Fetch..."
                ? "Fetch 完成"
                : FetchProgressText;
        }
    }

    public void Dispose()
    {
        _cacheLog?.Dispose();
        _gitService?.Dispose();
    }
}
