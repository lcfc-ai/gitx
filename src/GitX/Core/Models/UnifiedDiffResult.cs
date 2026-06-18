namespace GitX.Core.Models;

/// <summary>
/// 统一 diff 结果。
/// </summary>
public sealed class UnifiedDiffResult
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<UnifiedDiffRow> Rows { get; init; } = Array.Empty<UnifiedDiffRow>();

    public bool HasDifferences => Rows.Any(r => r.Kind != UnifiedDiffRowKind.Context);
}
