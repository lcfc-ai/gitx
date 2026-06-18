namespace GitX.Core.Models;

/// <summary>
/// 统一 diff 的单行数据。
/// </summary>
public sealed class UnifiedDiffRow
{
    public UnifiedDiffRowKind Kind { get; init; }

    public int? OldLineNumber { get; init; }

    public int? NewLineNumber { get; init; }

    public string Content { get; init; } = string.Empty;

    public string DisplayText { get; init; } = string.Empty;

    public int ContentOffset { get; init; }

    public IReadOnlyList<UnifiedDiffSpan> InlineSpans { get; init; } = Array.Empty<UnifiedDiffSpan>();
}

public enum UnifiedDiffRowKind
{
    Context,
    Added,
    Deleted
}
