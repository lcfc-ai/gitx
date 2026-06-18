using GitX.Core.Logging;
using GitX.Core.Models;
using LibGit2Sharp;
using System.IO;

namespace GitX.Services;

/// <summary>
/// Git 仓库基础能力：打开仓库、读取分支、读取提交和文件内容。
/// </summary>
public partial class GitService : IDisposable
{
    private readonly Repository _repo;
    private readonly string _repoPath;
    private readonly object _repoLock = new();
    private bool _disposed;

    public GitService(string repoPath)
    {
        _repoPath = repoPath;
        _repo = new Repository(repoPath);
        AppLog.Info("GitService 初始化，仓库路径: {Path}", repoPath);
    }

    /// <summary>
    /// 获取所有分支列表（本地+远程）
    /// </summary>
    public List<BranchModel> GetAllBranches()
    {
        List<BranchModel> list;
        lock (_repoLock)
        {
            list = new List<BranchModel>();
            var current = _repo.Head.FriendlyName;

            foreach (var branch in _repo.Branches)
            {
                var displayName = branch.FriendlyName;
                var isLocal = !branch.IsRemote;

                list.Add(new BranchModel
                {
                    FullName = branch.FriendlyName,
                    DisplayName = displayName,
                    IsLocalBranch = isLocal,
                    TipCommitId = branch.Tip?.Id,
                    IsCurrent = isLocal && branch.FriendlyName == current
                });
            }
        }

        return list
            .OrderBy(b => b.IsLocalBranch ? 0 : 1)
            .ThenBy(b => b.DisplayName)
            .ToList();
    }

    /// <summary>
    /// 获取当前本地分支名称
    /// </summary>
    public string GetCurrentBranchName()
    {
        lock (_repoLock)
        {
            return _repo.Head.FriendlyName;
        }
    }

    /// <summary>
    /// 获取分支对应 Commit（公开方法，自动加锁）
    /// </summary>
    public Commit? GetBranchCommit(string branchName)
    {
        lock (_repoLock)
        {
            return GetBranchCommitInternal(branchName);
        }
    }

    /// <summary>
    /// 获取分支下文件的 Blob 内容（文本）
    /// </summary>
    public (string content, bool isBinary) GetFileContent(string branchName, string filePath)
    {
        lock (_repoLock)
        {
            var blob = GetFileBlobInternal(branchName, filePath);
            if (blob == null) return (string.Empty, false);

            if (blob.IsBinary)
                return (string.Empty, true);

            var content = blob.GetContentText();
            return (content ?? string.Empty, false);
        }
    }

    /// <summary>
    /// 获取分支下文件的 Blob 对象（用于二进制流式写入，避免文本编码损坏）。
    /// 返回 null 表示目标分支中不存在该文件（视为删除）。
    /// 注意：Blob 生命周期由 Repository 管理，调用方不要 Dispose。
    /// </summary>
    public Blob? GetFileBlob(string branchName, string filePath)
    {
        lock (_repoLock)
        {
            return GetFileBlobInternal(branchName, filePath);
        }
    }

    /// <summary>
    /// 获取本地工作区文件内容。
    /// 返回空文本时不代表文件不存在，需要结合 isBinary 判断。
    /// </summary>
    public (string content, bool isBinary) GetWorkingFileContent(string filePath)
    {
        var fullPath = Path.Combine(_repoPath, filePath);
        if (!File.Exists(fullPath)) return (string.Empty, false);

        var bytes = File.ReadAllBytes(fullPath);
        if (IsBinary(bytes))
        {
            return (string.Empty, true);
        }

        return (File.ReadAllText(fullPath), false);
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length == 0) return false;

        var sampleLength = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sampleLength; i++)
        {
            if (bytes[i] == 0) return true;
        }

        return false;
    }

    /// <summary>
    /// 获取分支对应 Commit（内部方法，调用方需自行持有 _repoLock）
    /// </summary>
    private Commit? GetBranchCommitInternal(string branchName)
    {
        var branch = _repo.Branches[branchName];
        if (branch != null) return branch.Tip;

        branch = _repo.Branches.FirstOrDefault(b => b.FriendlyName == branchName || b.FriendlyName.EndsWith($"/{branchName}"));
        return branch?.Tip;
    }

    /// <summary>
    /// 获取分支下文件的 Blob（内部方法，调用方需自行持有 _repoLock）
    /// </summary>
    private Blob? GetFileBlobInternal(string branchName, string filePath)
    {
        var commit = GetBranchCommitInternal(branchName);
        if (commit == null) return null;

        var treeEntry = commit[filePath];
        if (treeEntry?.TargetType != TreeEntryTargetType.Blob)
            return null;

        return (Blob)treeEntry.Target;
    }

    public void Dispose()
    {
        lock (_repoLock)
        {
            if (!_disposed)
            {
                _repo.Dispose();
                _disposed = true;
            }
        }
        GC.SuppressFinalize(this);
    }
}
