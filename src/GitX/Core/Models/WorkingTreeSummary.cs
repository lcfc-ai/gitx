namespace GitX.Core.Models;

/// <summary>
/// 本地工作区相对目标分支的残余状态摘要。
/// </summary>
public sealed record WorkingTreeSummary(
    int AddedCount,
    int DeletedCount,
    int ModifiedCount,
    int RenamedCount,
    int ResidualCountOverride = -1)
{
    public int ResidualCount => ResidualCountOverride >= 0
        ? ResidualCountOverride
        : AddedCount + DeletedCount + ModifiedCount + RenamedCount;

    public bool IsClean => ResidualCount == 0;

    public string SummaryText => IsClean
        ? "本地工作区已对齐对比分支"
        : $"本地工作区仍有 {ResidualCount} 个差异";

    public string DetailText
    {
        get
        {
            if (IsClean)
            {
                return "没有未覆盖的本地改动";
            }

            var parts = new List<string>();
            if (AddedCount > 0) parts.Add($"新增 {AddedCount}");
            if (ModifiedCount > 0) parts.Add($"修改 {ModifiedCount}");
            if (DeletedCount > 0) parts.Add($"删除 {DeletedCount}");
            if (RenamedCount > 0) parts.Add($"重命名 {RenamedCount}");

            return string.Join(" · ", parts);
        }
    }
}
