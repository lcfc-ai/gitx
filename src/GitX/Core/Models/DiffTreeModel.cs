using LibGit2Sharp;
using System.Collections.ObjectModel;

namespace GitX.Core.Models;

/// <summary>
/// 差异树模型（文件/文件夹节点）
/// </summary>
public class DiffTreeModel
{
    /// <summary>
    /// 显示名称（文件夹名/文件名）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 完整相对仓库路径
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否文件
    /// </summary>
    public bool IsFile { get; set; }

    /// <summary>
    /// 文件变更类型（文件夹为 null）
    /// </summary>
    public ChangeKind? ChangeType { get; set; }

    /// <summary>
    /// 变更前路径，仅用于重命名/复制等场景。
    /// </summary>
    public string? OldPath { get; set; }

    /// <summary>
    /// 子节点集合
    /// </summary>
    public ObservableCollection<DiffTreeModel> Children { get; set; } = new();

    /// <summary>
    /// 是否展开节点
    /// </summary>
    public bool IsExpanded { get; set; } = false;

    /// <summary>
    /// 文件夹节点：内部变更文件数量
    /// </summary>
    public int ChangeFileCount { get; set; }

    /// <summary>
    /// 内部新增文件数量
    /// </summary>
    public int AddedFileCount { get; set; }

    /// <summary>
    /// 内部删除文件数量
    /// </summary>
    public int DeletedFileCount { get; set; }

    /// <summary>
    /// 内部修改文件数量
    /// </summary>
    public int ModifiedFileCount { get; set; }

    /// <summary>
    /// 内部重命名文件数量
    /// </summary>
    public int RenamedFileCount { get; set; }

    /// <summary>
    /// 变更类型中文描述
    /// </summary>
    public string ChangeTypeDesc => ChangeType switch
    {
        ChangeKind.Added => "新增",
        ChangeKind.Modified => "修改",
        ChangeKind.Deleted => "移除",
        ChangeKind.Renamed => "重命名",
        ChangeKind.Copied => "复制",
        ChangeKind.TypeChanged => "类型变更",
        _ => "未知"
    };

    /// <summary>
    /// 变更类型短码：+/-/x/m
    /// </summary>
    public string ChangeTypeCode => ChangeType switch
    {
        ChangeKind.Added => "+",
        ChangeKind.Deleted => "x",
        ChangeKind.Modified => "m",
        ChangeKind.Renamed => "r",
        ChangeKind.Copied => "c",
        ChangeKind.TypeChanged => "t",
        _ => "?"
    };

    /// <summary>
    /// 变更类型颜色
    /// </summary>
    public string ChangeTypeColor => ChangeType switch
    {
        ChangeKind.Added => "#A6E3A1",
        ChangeKind.Modified => "#F9E2AF",
        ChangeKind.Deleted => "#F38BA8",
        ChangeKind.Renamed => "#CBA6F7",
        _ => "#6C7086"
    };

    /// <summary>
    /// 树节点显示颜色：文件按变更类型着色，文件夹保持默认文本色。
    /// </summary>
    public string DisplayForegroundColor => IsFile ? ChangeTypeColor : "#D7D1C5";

    /// <summary>
    /// 文件变更胶囊背景色。
    /// </summary>
    public string BadgeBackgroundColor => ChangeType switch
    {
        ChangeKind.Added => "#263026",
        ChangeKind.Modified => "#322E22",
        ChangeKind.Deleted => "#342321",
        ChangeKind.Renamed => "#2F2634",
        ChangeKind.Copied => "#26313A",
        ChangeKind.TypeChanged => "#2F2B1F",
        _ => "#2D2924"
    };

    /// <summary>
    /// 文件夹统计颜色：按内部主要变更类型着色。
    /// </summary>
    public string FolderStatsColor => IsFile ? ChangeTypeColor : DominantChangeTypeColor;

    /// <summary>
    /// 文件夹统计胶囊背景色。
    /// </summary>
    public string FolderStatsBackgroundColor => IsFile ? BadgeBackgroundColor : "#2D2924";

    /// <summary>
    /// 文件夹中占比最高的变更类型。
    /// </summary>
    public ChangeKind DominantChangeType => GetDominantChangeType();

    /// <summary>
    /// 文件夹中占比最高的变更类型颜色。
    /// </summary>
    public string DominantChangeTypeColor => DominantChangeType switch
    {
        ChangeKind.Added => "#A6E3A1",
        ChangeKind.Modified => "#F9E2AF",
        ChangeKind.Deleted => "#F38BA8",
        ChangeKind.Renamed => "#CBA6F7",
        _ => "#6C7086"
    };

    /// <summary>
    /// 展开中的文件夹背景色。
    /// </summary>
    public string ExpandedFolderBackgroundColor => !IsFile && IsExpanded ? "#26231F" : "Transparent";

    /// <summary>
    /// 文件夹统计简述，例如 a3 d1 m2。
    /// </summary>
    public string FolderStatsSummary
    {
        get
        {
            if (IsFile) return ChangeTypeCode;

            var parts = new List<string>();
            if (AddedFileCount > 0) parts.Add($"+{AddedFileCount}");
            if (DeletedFileCount > 0) parts.Add($"-{DeletedFileCount}");
            if (ModifiedFileCount > 0) parts.Add($"m{ModifiedFileCount}");
            if (RenamedFileCount > 0) parts.Add($"r{RenamedFileCount}");
            return parts.Count > 0 ? string.Join(" ", parts) : "0";
        }
    }

    private ChangeKind GetDominantChangeType()
    {
        var counts = new Dictionary<ChangeKind, int>
        {
            [ChangeKind.Added] = AddedFileCount,
            [ChangeKind.Deleted] = DeletedFileCount,
            [ChangeKind.Modified] = ModifiedFileCount,
            [ChangeKind.Renamed] = RenamedFileCount
        };

        var max = counts.MaxBy(kvp => kvp.Value);
        if (max.Value > 0) return max.Key;

        return ChangeType ?? ChangeKind.Modified;
    }
}
