using GitX.Core.Models;
using System.Text.RegularExpressions;

namespace GitX.Services;

/// <summary>
/// 文本差异服务，生成统一 diff 视图所需的数据。
/// </summary>
public partial class TextDiffService
{
    private const int ContextLines = 3;
    private static readonly Regex TokenRegex = new(@"\s+|\w+|[^\w\s]+", RegexOptions.Compiled);

    public UnifiedDiffResult BuildUnifiedDiff(string leftText, string rightText)
    {
        var leftLines = SplitLines(leftText);
        var rightLines = SplitLines(rightText);
        var maxLineNumberWidth = Math.Max(3, Math.Max(leftLines.Count, rightLines.Count).ToString().Length);

        var ops = AlignLines(leftLines, rightLines);
        var rows = BuildRows(ops, maxLineNumberWidth);
        var text = string.Join(Environment.NewLine, rows.Select(r => r.DisplayText));

        return new UnifiedDiffResult
        {
            Text = text,
            Rows = rows
        };
    }

    private static List<string> SplitLines(string text)
    {
        text ??= string.Empty;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Split('\n').ToList();
    }

    private static List<LineOp> AlignLines(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var dp = BuildLcsTable(left, right);
        var ops = new List<LineOp>();
        var i = 0;
        var j = 0;

        while (i < left.Count && j < right.Count)
        {
            if (string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                ops.Add(LineOp.Equal(left[i], right[j], i + 1, j + 1));
                i++;
                j++;
                continue;
            }

            if (dp[i + 1, j] >= dp[i, j + 1])
            {
                ops.Add(LineOp.Delete(left[i], i + 1));
                i++;
            }
            else
            {
                ops.Add(LineOp.Add(right[j], j + 1));
                j++;
            }
        }

        while (i < left.Count)
        {
            ops.Add(LineOp.Delete(left[i], i + 1));
            i++;
        }

        while (j < right.Count)
        {
            ops.Add(LineOp.Add(right[j], j + 1));
            j++;
        }

        return ops;
    }

    private static int[,] BuildLcsTable(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var dp = new int[left.Count + 1, right.Count + 1];

        for (int i = left.Count - 1; i >= 0; i--)
        {
            for (int j = right.Count - 1; j >= 0; j--)
            {
                if (string.Equals(left[i], right[j], StringComparison.Ordinal))
                {
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }
        }

        return dp;
    }

    private static List<UnifiedDiffRow> BuildRows(List<LineOp> ops, int width)
    {
        var rows = new List<UnifiedDiffRow>();
        int oldLine = 1;
        int newLine = 1;

        for (int i = 0; i < ops.Count;)
        {
            var op = ops[i];
            if (op.Kind == LineOpKind.Equal)
            {
                rows.Add(CreateRow(UnifiedDiffRowKind.Context, oldLine, newLine, op.Text, width, Array.Empty<UnifiedDiffSpan>()));
                oldLine++;
                newLine++;
                i++;
                continue;
            }

            var deleteBlock = new List<LineOp>();
            var addBlock = new List<LineOp>();
            while (i < ops.Count && ops[i].Kind != LineOpKind.Equal)
            {
                if (ops[i].Kind == LineOpKind.Delete) deleteBlock.Add(ops[i]);
                else addBlock.Add(ops[i]);
                i++;
            }

            var pairCount = Math.Min(deleteBlock.Count, addBlock.Count);
            for (int p = 0; p < pairCount; p++)
            {
                var left = deleteBlock[p];
                var right = addBlock[p];
                var leftSpans = BuildInlineSpans(left.Text, right.Text);
                var rightSpans = BuildInlineSpans(right.Text, left.Text);

                rows.Add(CreateRow(UnifiedDiffRowKind.Deleted, oldLine, null, left.Text, width, leftSpans));
                rows.Add(CreateRow(UnifiedDiffRowKind.Added, null, newLine, right.Text, width, rightSpans));
                oldLine++;
                newLine++;
            }

            for (int p = pairCount; p < deleteBlock.Count; p++)
            {
                rows.Add(CreateRow(UnifiedDiffRowKind.Deleted, oldLine, null, deleteBlock[p].Text, width, Array.Empty<UnifiedDiffSpan>()));
                oldLine++;
            }

            for (int p = pairCount; p < addBlock.Count; p++)
            {
                rows.Add(CreateRow(UnifiedDiffRowKind.Added, null, newLine, addBlock[p].Text, width, Array.Empty<UnifiedDiffSpan>()));
                newLine++;
            }
        }

        return TrimContext(rows, ContextLines);
    }

    private static List<UnifiedDiffRow> TrimContext(List<UnifiedDiffRow> rows, int contextLines)
    {
        var keep = new bool[rows.Count];
        var changeIndices = rows
            .Select((row, index) => (row, index))
            .Where(x => x.row.Kind != UnifiedDiffRowKind.Context)
            .Select(x => x.index)
            .ToList();

        if (changeIndices.Count == 0)
        {
            return rows;
        }

        foreach (var index in changeIndices)
        {
            var start = Math.Max(0, index - contextLines);
            var end = Math.Min(rows.Count - 1, index + contextLines);
            for (int i = start; i <= end; i++)
            {
                keep[i] = true;
            }
        }

        var trimmed = new List<UnifiedDiffRow>();
        var lastKept = -2;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            if (lastKept >= 0 && i > lastKept + 1)
            {
                trimmed.Add(new UnifiedDiffRow
                {
                    Kind = UnifiedDiffRowKind.Context,
                    Content = "...",
                    DisplayText = "...",
                    ContentOffset = 0,
                    InlineSpans = Array.Empty<UnifiedDiffSpan>()
                });
            }

            trimmed.Add(rows[i]);
            lastKept = i;
        }

        return trimmed;
    }

    private static UnifiedDiffRow CreateRow(
        UnifiedDiffRowKind kind,
        int? oldLine,
        int? newLine,
        string content,
        int width,
        IReadOnlyList<UnifiedDiffSpan> spans)
    {
        var oldText = oldLine.HasValue ? oldLine.Value.ToString().PadLeft(width) : new string(' ', width);
        var newText = newLine.HasValue ? newLine.Value.ToString().PadLeft(width) : new string(' ', width);
        var sign = kind switch
        {
            UnifiedDiffRowKind.Context => " ",
            UnifiedDiffRowKind.Added => "+",
            UnifiedDiffRowKind.Deleted => "-",
            _ => " "
        };

        var display = $"{oldText} {newText} {sign} {content}";
        var contentOffset = display.IndexOf(content, StringComparison.Ordinal);
        if (contentOffset < 0)
        {
            contentOffset = display.Length;
        }

        return new UnifiedDiffRow
        {
            Kind = kind,
            OldLineNumber = oldLine,
            NewLineNumber = newLine,
            Content = content,
            DisplayText = display,
            ContentOffset = contentOffset,
            InlineSpans = spans
        };
    }

    private static IReadOnlyList<UnifiedDiffSpan> BuildInlineSpans(string primary, string secondary)
    {
        var primaryTokens = Tokenize(primary);
        var secondaryTokens = Tokenize(secondary);

        if (primaryTokens.Count == 0)
        {
            return Array.Empty<UnifiedDiffSpan>();
        }

        var dp = BuildLcsTable(primaryTokens.Select(t => t.Text).ToList(), secondaryTokens.Select(t => t.Text).ToList());
        var matched = new bool[primaryTokens.Count];

        var i = 0;
        var j = 0;
        while (i < primaryTokens.Count && j < secondaryTokens.Count)
        {
            if (string.Equals(primaryTokens[i].Text, secondaryTokens[j].Text, StringComparison.Ordinal))
            {
                matched[i] = true;
                i++;
                j++;
                continue;
            }

            if (dp[i + 1, j] >= dp[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        var spans = new List<UnifiedDiffSpan>();
        int? currentStart = null;
        int currentEnd = 0;

        for (int index = 0; index < primaryTokens.Count; index++)
        {
            var token = primaryTokens[index];
            var highlight = !matched[index] && !string.IsNullOrWhiteSpace(token.Text);
            if (!highlight)
            {
                if (currentStart.HasValue)
                {
                    spans.Add(new UnifiedDiffSpan(currentStart.Value, currentEnd - currentStart.Value));
                    currentStart = null;
                }
                continue;
            }

            if (!currentStart.HasValue)
            {
                currentStart = token.Start;
            }

            currentEnd = token.Start + token.Length;
        }

        if (currentStart.HasValue)
        {
            spans.Add(new UnifiedDiffSpan(currentStart.Value, currentEnd - currentStart.Value));
        }

        return spans;
    }

    private static List<Token> Tokenize(string text)
    {
        var list = new List<Token>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        foreach (Match match in TokenRegex.Matches(text))
        {
            list.Add(new Token(match.Value, match.Index, match.Length));
        }

        return list;
    }

    private sealed record Token(string Text, int Start, int Length);

    private sealed record LineOp(LineOpKind Kind, string Text, int? OldLine, int? NewLine)
    {
        public static LineOp Equal(string left, string right, int oldLine, int newLine) => new(LineOpKind.Equal, left, oldLine, newLine);
        public static LineOp Delete(string text, int oldLine) => new(LineOpKind.Delete, text, oldLine, null);
        public static LineOp Add(string text, int newLine) => new(LineOpKind.Add, text, null, newLine);
    }

    private enum LineOpKind
    {
        Equal,
        Delete,
        Add
    }
}
