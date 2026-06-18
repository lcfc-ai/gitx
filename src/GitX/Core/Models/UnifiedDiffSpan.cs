namespace GitX.Core.Models;

/// <summary>
/// 统一 diff 中的行内高亮片段。
/// Start 和 Length 都相对于该行的内容文本，不含行号列。
/// </summary>
public sealed record UnifiedDiffSpan(int Start, int Length);
