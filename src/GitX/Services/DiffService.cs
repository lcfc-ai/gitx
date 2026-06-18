using GitX.Core.Logging;
using GitX.Core.Models;
using LibGit2Sharp;

namespace GitX.Services;

/// <summary>
/// 差异计算服务，构建过滤型目录树
/// </summary>
public class DiffService
{
    private readonly GitService _gitService;

    public DiffService(GitService gitService)
    {
        _gitService = gitService;
    }

    /// <summary>
    /// 计算「当前分支最新 commit」与「目标分支最新 commit」的差异，构建过滤后的树形模型。
    /// 只显示两个分支真正分叉的文件；不包含工作区未提交的改动。
    /// 覆盖语义：用目标分支版本替换工作区文件。
    ///
    /// 差异来源由 GitService 决定：优先 git CLI（结果与命令行/IDE 一致，正确处理
    /// .gitattributes 的 text 规范化和 core.autocrlf），LibGit2Sharp 兜底。
    /// </summary>
    public DiffTreeModel BuildDiffTree(string currentBranch, string targetBranch)
    {
        AppLog.Info("开始计算差异: {Current} vs {Target}", currentBranch, targetBranch);

        var changes = _gitService.GetTreeChanges(currentBranch, targetBranch);
        var root = new DiffTreeModel
        {
            Name = "Root",
            FullPath = "",
            IsFile = false
        };

        foreach (var change in changes)
        {
            // 重命名/复制场景：change.Path 为目标侧新路径，OldPath 为旧路径。
            // 覆盖语义以「目标分支中文件所在路径」为准，故用 change.Path。
            AddFileToTree(root, change.Path, change.Status);
        }

        PopulateChangeCounts(root);

        AppLog.Info("差异计算完成，共 {Count} 个变更文件", changes.Count);
        return root;
    }

    /// <summary>
    /// 将文件按路径添加到树形结构
    /// </summary>
    private void AddFileToTree(DiffTreeModel root, string filePath, ChangeKind changeType)
    {
        var parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var current = root;
        var pathBuilder = new System.Text.StringBuilder();

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) pathBuilder.Append('/');
            pathBuilder.Append(parts[i]);
            var currentPath = pathBuilder.ToString();
            var isFile = i == parts.Length - 1;

            var existing = current.Children.FirstOrDefault(c => c.Name == parts[i]);
            if (existing == null)
            {
                var node = new DiffTreeModel
                {
                    Name = parts[i],
                    FullPath = currentPath,
                    IsFile = isFile,
                    ChangeType = isFile ? changeType : null
                };
                current.Children.Add(node);
                current = node;
            }
            else
            {
                current = existing;
            }
        }
    }

    private static int PopulateChangeCounts(DiffTreeModel node)
    {
        if (node.IsFile)
        {
            node.ChangeFileCount = 1;
            node.AddedFileCount = node.ChangeType == ChangeKind.Added ? 1 : 0;
            node.DeletedFileCount = node.ChangeType == ChangeKind.Deleted ? 1 : 0;
            node.ModifiedFileCount = node.ChangeType == ChangeKind.Modified ? 1 : 0;
            node.RenamedFileCount = node.ChangeType == ChangeKind.Renamed ? 1 : 0;
            return 1;
        }

        var count = 0;
        var added = 0;
        var deleted = 0;
        var modified = 0;
        var renamed = 0;
        foreach (var child in node.Children)
        {
            count += PopulateChangeCounts(child);
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
}
