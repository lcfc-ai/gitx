namespace GitX.Core.Models;

/// <summary>
/// 某次提交的完整详情。
/// </summary>
public sealed class CommitDetailResult
{
    public CommitHistoryItem Commit { get; set; } = new();
    public IReadOnlyList<CommitFileChangeItem> Files { get; set; } = Array.Empty<CommitFileChangeItem>();
}
