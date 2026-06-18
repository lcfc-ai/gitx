using LibGit2Sharp;

namespace GitX.Core.Models;

/// <summary>
/// 某次提交里受影响的文件。
/// </summary>
public sealed class CommitFileChangeItem
{
    public string Path { get; set; } = string.Empty;
    public string? OldPath { get; set; }
    public ChangeKind Status { get; set; }

    public string DisplayPath => string.IsNullOrWhiteSpace(OldPath) || string.Equals(OldPath, Path, StringComparison.OrdinalIgnoreCase)
        ? Path
        : $"{OldPath} -> {Path}";

    public string StatusCode => Status switch
    {
        ChangeKind.Added => "+",
        ChangeKind.Modified => "m",
        ChangeKind.Deleted => "x",
        ChangeKind.Renamed => "r",
        ChangeKind.Copied => "c",
        ChangeKind.TypeChanged => "t",
        _ => "?"
    };
}
