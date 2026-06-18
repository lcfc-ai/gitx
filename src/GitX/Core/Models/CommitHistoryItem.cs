namespace GitX.Core.Models;

/// <summary>
/// 某个路径在目标分支上的提交记录。
/// </summary>
public sealed class CommitHistoryItem
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset CommitTime { get; set; }
    public string Message { get; set; } = string.Empty;
    public string MessageShort => Message.Length <= 80 ? Message : Message[..77] + "...";
    public string CommitTimeText => CommitTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
