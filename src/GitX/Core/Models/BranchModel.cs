using LibGit2Sharp;

namespace GitX.Core.Models;

/// <summary>
/// 分支模型
/// </summary>
public class BranchModel
{
    /// <summary>
    /// 分支完整名称
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称（远程分支去除 remotes/origin/ 前缀）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 是否本地分支
    /// </summary>
    public bool IsLocalBranch { get; set; }

    /// <summary>
    /// Git 提交 Hash
    /// </summary>
    public ObjectId? TipCommitId { get; set; }

    /// <summary>
    /// 是否当前分支
    /// </summary>
    public bool IsCurrent { get; set; }
}
