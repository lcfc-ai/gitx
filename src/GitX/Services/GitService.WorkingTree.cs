using GitX.Core.Models;
using LibGit2Sharp;
using System.IO;

namespace GitX.Services;

public partial class GitService
{
    /// <summary>
    /// 获取本地工作区的残余状态摘要，用于解释“下载全部后为什么还有差别”。
    /// </summary>
    public WorkingTreeSummary GetWorkingTreeSummary(string currentBranchName, string targetBranchName)
    {
        var changes = GetTreeChanges(currentBranchName, targetBranchName);
        return new WorkingTreeSummary(
            AddedCount: changes.Count(c => c.Status == ChangeKind.Added),
            DeletedCount: changes.Count(c => c.Status == ChangeKind.Deleted),
            ModifiedCount: changes.Count(c => c.Status == ChangeKind.Modified),
            RenamedCount: changes.Count(c => c.Status == ChangeKind.Renamed));
    }

    /// <summary>
    /// 计算本地文件是否已经与目标分支一致。
    /// </summary>
    private bool WorkingFileMatchesTarget(string targetBranchName, string filePath)
    {
        lock (_repoLock)
        {
            var targetBlob = GetFileBlobInternal(targetBranchName, filePath);
            if (targetBlob == null) return false;

            var fullPath = Path.Combine(_repoPath, filePath);
            if (!File.Exists(fullPath)) return false;

            if (targetBlob.IsBinary)
            {
                using var targetStream = targetBlob.GetContentStream();
                using var targetMs = new MemoryStream();
                targetStream.CopyTo(targetMs);
                var targetBytes = targetMs.ToArray();
                var localBytes = File.ReadAllBytes(fullPath);
                return localBytes.SequenceEqual(targetBytes);
            }

            var targetText = NormalizeText(targetBlob.GetContentText() ?? string.Empty);
            var localText = NormalizeText(File.ReadAllText(fullPath));
            return string.Equals(localText, targetText, StringComparison.Ordinal);
        }
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
