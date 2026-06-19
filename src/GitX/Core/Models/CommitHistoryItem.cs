namespace GitX.Core.Models;

/// <summary>
/// 某个路径在目标分支上的提交记录。
/// </summary>
public sealed class CommitHistoryItem
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
    public string Author { get; set; } = string.Empty;
    public string AuthorEmail { get; set; } = string.Empty;
    public string AuthorDisplay => string.IsNullOrWhiteSpace(AuthorEmail)
        ? Author
        : $"{Author} <{AuthorEmail}>";
    public DateTimeOffset CommitTime { get; set; }
    public string Message { get; set; } = string.Empty;
    public string MessageShort => Message.Length <= 80 ? Message : Message[..77] + "...";
    public string CommitTimeText => CommitTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CommitTimeRelative
    {
        get
        {
            var local = CommitTime.ToLocalTime();
            var diff = DateTimeOffset.Now - local;
            if (diff.TotalSeconds < 60) return "刚刚";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
            if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
            if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} 个月前";
            return $"{(int)(diff.TotalDays / 365)} 年前";
        }
    }
    public IReadOnlyList<string> ParentShas { get; set; } = Array.Empty<string>();
    public string ParentShasText => ParentShas.Count == 0
        ? "(根提交)"
        : ParentShas.Count == 1
            ? ParentShas[0][..Math.Min(8, ParentShas[0].Length)]
            : $"{ParentShas[0][..Math.Min(8, ParentShas[0].Length)]} +{ParentShas.Count - 1}";
}
