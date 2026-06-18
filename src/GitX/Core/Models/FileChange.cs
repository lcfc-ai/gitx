using LibGit2Sharp;

namespace GitX.Core.Models;

/// <summary>
/// 单个文件的变更信息（与底层 git 实现无关的中性模型）。
/// Path 为目标侧的新路径；OldPath 仅在 Renamed/Copied 时有值。
/// </summary>
public record FileChange(string Path, string? OldPath, ChangeKind Status);
