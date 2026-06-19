using GitX.Core.Models;
using System.Collections.ObjectModel;

namespace GitX.Services;

/// <summary>
/// 差异树筛选服务：按关键字过滤可见节点，并保留父级路径。
/// </summary>
public sealed class DiffTreeFilterService
{
    public ObservableCollection<DiffTreeModel> Filter(IEnumerable<DiffTreeModel> roots, string? filterText)
    {
        var (list, _) = FilterWithCount(roots, filterText);
        return list;
    }

    /// <summary>
    /// 与 Filter 相同，但额外返回根级 visible count（已累加）。
    /// 省去调用方再 LINQ Sum 一遍整棵树。
    /// </summary>
    public (ObservableCollection<DiffTreeModel> roots, int visibleCount) FilterWithCount(IEnumerable<DiffTreeModel> roots, string? filterText)
    {
        var list = new ObservableCollection<DiffTreeModel>();
        var term = Normalize(filterText);
        int total = 0;

        foreach (var root in roots)
        {
            var filtered = FilterNode(root, term);
            if (filtered != null)
            {
                list.Add(filtered);
                total += filtered.ChangeFileCount;
            }
        }

        return (list, total);
    }

    private static DiffTreeModel? FilterNode(DiffTreeModel node, string term)
    {
        var clonedChildren = new List<DiffTreeModel>();
        foreach (var child in node.Children)
        {
            var filteredChild = FilterNode(child, term);
            if (filteredChild != null)
            {
                clonedChildren.Add(filteredChild);
            }
        }

        var matches = string.IsNullOrEmpty(term) || Matches(node, term);
        if (!matches && clonedChildren.Count == 0)
        {
            return null;
        }

        var clone = CloneShallow(node);
        clone.Children = new ObservableCollection<DiffTreeModel>(clonedChildren);
        // 累加子节点已有 counts：避免对整棵树重新枚举
        AccumulateCounts(clone, clonedChildren);
        return clone;
    }

    private static void AccumulateCounts(DiffTreeModel clone, List<DiffTreeModel> children)
    {
        if (clone.IsFile)
        {
            // 文件节点：CloneShallow 已复制源节点 counts
            return;
        }

        int count = 0, added = 0, deleted = 0, modified = 0, renamed = 0;
        foreach (var child in children)
        {
            count += child.ChangeFileCount;
            added += child.AddedFileCount;
            deleted += child.DeletedFileCount;
            modified += child.ModifiedFileCount;
            renamed += child.RenamedFileCount;
        }
        clone.ChangeFileCount = count;
        clone.AddedFileCount = added;
        clone.DeletedFileCount = deleted;
        clone.ModifiedFileCount = modified;
        clone.RenamedFileCount = renamed;
    }

    private static bool Matches(DiffTreeModel node, string term)
    {
        return node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(node.OldPath) && node.OldPath.Contains(term, StringComparison.OrdinalIgnoreCase))
            || node.ChangeTypeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.ChangeTypeDesc.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.FolderStatsSummary.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static DiffTreeModel CloneShallow(DiffTreeModel source)
    {
        return new DiffTreeModel
        {
            Name = source.Name,
            FullPath = source.FullPath,
            IsFile = source.IsFile,
            ChangeType = source.ChangeType,
            OldPath = source.OldPath,
            IsExpanded = source.IsExpanded,
            ChangeFileCount = source.ChangeFileCount,
            AddedFileCount = source.AddedFileCount,
            DeletedFileCount = source.DeletedFileCount,
            ModifiedFileCount = source.ModifiedFileCount,
            RenamedFileCount = source.RenamedFileCount
        };
    }

    private static int RecalculateCounts(DiffTreeModel node)
    {
        if (node.IsFile)
        {
            node.ChangeFileCount = 1;
            node.AddedFileCount = node.ChangeType == LibGit2Sharp.ChangeKind.Added ? 1 : 0;
            node.DeletedFileCount = node.ChangeType == LibGit2Sharp.ChangeKind.Deleted ? 1 : 0;
            node.ModifiedFileCount = node.ChangeType == LibGit2Sharp.ChangeKind.Modified ? 1 : 0;
            node.RenamedFileCount = node.ChangeType == LibGit2Sharp.ChangeKind.Renamed ? 1 : 0;
            return 1;
        }

        var count = 0;
        var added = 0;
        var deleted = 0;
        var modified = 0;
        var renamed = 0;

        foreach (var child in node.Children)
        {
            count += RecalculateCounts(child);
            added += child.AddedFileCount;
            deleted += child.DeletedFileCount;
            modified += child.ModifiedFileCount;
            renamed += child.RenamedFileCount;
        }

        node.ChangeFileCount = count;
        node.AddedFileCount = added;
        node.DeletedFileCount = deleted;
        node.ModifiedFileCount = modified;
        node.RenamedFileCount = renamed;
        return count;
    }

    private static string Normalize(string? text)
    {
        return (text ?? string.Empty).Trim();
    }

    /// <summary>
    /// 在树中按完整路径查找节点（用于「点 commit 影响文件跳到对应文件」）。
    /// </summary>
    public DiffTreeModel? FindNodeByPath(IEnumerable<DiffTreeModel> roots, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        foreach (var root in roots)
        {
            var hit = FindNodeByPathRecursive(root, fullPath);
            if (hit != null) return hit;
        }
        return null;
    }

    private static DiffTreeModel? FindNodeByPathRecursive(DiffTreeModel node, string fullPath)
    {
        if (string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            var hit = FindNodeByPathRecursive(child, fullPath);
            if (hit != null) return hit;
        }
        return null;
    }
}
