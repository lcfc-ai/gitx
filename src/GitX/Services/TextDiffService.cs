using GitX.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace GitX.Services;

/// <summary>
/// 文本差异服务，生成统一 diff 视图所需的数据。
/// 大文件场景下：
/// - 行级对齐使用 Myers diff（O((N+D)·D) 内存，远优于 LCS 的 O(N·M)）
/// - 行内 token 对齐在两端差异巨大或一方为空时直接整段高亮
/// - 全等行跳过 token 化
/// </summary>
public partial class TextDiffService
{
    private const int ContextLines = 3;
    // 一对 inline 差异达到这个 token 数上限就不再做词级 diff，整行高亮即可
    private const int MaxInlineTokens = 600;
    // 长度比超过这个比例视为「差异巨大」，跳过词级 diff
    private const double InlineLengthRatio = 3.0;
    private static readonly Regex TokenRegex = new(@"\s+|\w+|[^\w\s]+", RegexOptions.Compiled);

    public UnifiedDiffResult BuildUnifiedDiff(string leftText, string rightText)
    {
        var leftLines = SplitLines(leftText);
        var rightLines = SplitLines(rightText);
        var maxLineNumberWidth = Math.Max(3, Math.Max(leftLines.Count, rightLines.Count).ToString().Length);

        var ops = AlignLines(leftLines, rightLines);
        var rows = BuildRows(ops, maxLineNumberWidth);
        // 大文件：StringBuilder 拼一次，避免 LINQ + string.Join 中间数组
        var text = ConcatDisplayText(rows);

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
        // 空集合直接产出对齐序列
        if (left.Count == 0)
        {
            var adds = new List<LineOp>(right.Count);
            for (int j = 0; j < right.Count; j++)
            {
                adds.Add(LineOp.Add(right[j], j + 1));
            }
            return adds;
        }
        if (right.Count == 0)
        {
            var dels = new List<LineOp>(left.Count);
            for (int i = 0; i < left.Count; i++)
            {
                dels.Add(LineOp.Delete(left[i], i + 1));
            }
            return dels;
        }

        var ops = new List<LineOp>(Math.Max(left.Count, right.Count));
        MyersDiff(left, right, ops);
        return ops;
    }

    /// <summary>
    /// Myers diff：经典 O((N+M)·D) 算法。
    /// 轨迹保存 (v, goDown, k)，反推时利用决策方向与 v 推出每步 (prevX, prevY)，
    /// 中间的顺势消耗就是 Equal 行。
    /// </summary>
    private static void MyersDiff(IReadOnlyList<string> left, IReadOnlyList<string> right, List<LineOp> output)
    {
        var n = left.Count;
        var m = right.Count;
        var max = n + m;

        var v = new int[2 * max + 1];
        var offset = max;
        var trace = new List<(int[] v, bool goDown, int k)>();
        int endK = 0;

        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                bool goDown = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]);
                if (goDown)
                {
                    x = v[k + 1 + offset];
                }
                else
                {
                    x = v[k - 1 + offset] + 1;
                }

                int y = x - k;
                while (x < n && y < m && string.Equals(left[x], right[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;
                trace.Add(((int[])v.Clone(), goDown, k));

                if (x >= n && y >= m)
                {
                    endK = k;
                    Backtrack(left, right, trace, n, m, offset, endK, output);
                    return;
                }
            }
        }
    }

    private static void Backtrack(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        List<(int[] v, bool goDown, int k)> trace,
        int n,
        int m,
        int offset,
        int endK,
        List<LineOp> output)
    {
        var reverse = new List<LineOp>(n + m);
        int x = n, y = m;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            var (vv, goDown, k) = trace[d];
            int prevK = goDown ? k + 1 : k - 1;
            int prevX = vv[prevK + offset];
            int prevY = prevX - prevK;

            // 顺势 Equal 段
            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                reverse.Add(LineOp.Equal(left[x], right[y], x + 1, y + 1));
            }

            if (d > 0)
            {
                if (goDown)
                {
                    // 插入
                    y--;
                    reverse.Add(LineOp.Add(right[y], y + 1));
                }
                else
                {
                    // 删除
                    x--;
                    reverse.Add(LineOp.Delete(left[x], x + 1));
                }
            }
        }

        // 起点处还可能剩一段 Equal
        while (x > 0 && y > 0)
        {
            x--;
            y--;
            reverse.Add(LineOp.Equal(left[x], right[y], x + 1, y + 1));
        }
        while (x > 0)
        {
            x--;
            reverse.Add(LineOp.Delete(left[x], x + 1));
        }
        while (y > 0)
        {
            y--;
            reverse.Add(LineOp.Add(right[y], y + 1));
        }

        for (int i = reverse.Count - 1; i >= 0; i--)
        {
            output.Add(reverse[i]);
        }
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
                var (leftSpans, rightSpans) = BuildInlineSpansPair(left.Text, right.Text);

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
        // 单次扫描标记所有需要保留的位置，避免 LINQ 多轮枚举
        int firstChange = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != UnifiedDiffRowKind.Context)
            {
                if (firstChange < 0) firstChange = i;
                var start = Math.Max(0, i - contextLines);
                var end = Math.Min(rows.Count - 1, i + contextLines);
                for (int k = start; k <= end; k++)
                {
                    keep[k] = true;
                }
            }
        }

        if (firstChange < 0)
        {
            return rows;
        }

        var trimmed = new List<UnifiedDiffRow>(rows.Count);
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

    /// <summary>
    /// 同时计算双向 inline spans，比两次调用 BuildInlineSpans 节省一次 token 化。
    /// </summary>
    private static (IReadOnlyList<UnifiedDiffSpan> leftSpans, IReadOnlyList<UnifiedDiffSpan> rightSpans) BuildInlineSpansPair(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            var full = leftTokens.Count == 0
                ? Array.Empty<UnifiedDiffSpan>()
                : ToFullHighlightSpans(leftTokens);
            var fullRight = rightTokens.Count == 0
                ? Array.Empty<UnifiedDiffSpan>()
                : ToFullHighlightSpans(rightTokens);
            return (full, fullRight);
        }

        // 长度差异巨大时直接整段高亮，跳过 Myers
        var longer = Math.Max(leftTokens.Count, rightTokens.Count);
        var shorter = Math.Min(leftTokens.Count, rightTokens.Count);
        if (longer > MaxInlineTokens || (shorter > 0 && longer / shorter > InlineLengthRatio))
        {
            return (ToFullHighlightSpans(leftTokens), ToFullHighlightSpans(rightTokens));
        }

        // 整段完全相同：直接返回空
        if (leftTokens.Count == rightTokens.Count)
        {
            bool allSame = true;
            for (int i = 0; i < leftTokens.Count; i++)
            {
                if (!string.Equals(leftTokens[i].Text, rightTokens[i].Text, StringComparison.Ordinal))
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame) return (Array.Empty<UnifiedDiffSpan>(), Array.Empty<UnifiedDiffSpan>());
        }

        var leftMatched = new bool[leftTokens.Count];
        var rightMatched = new bool[rightTokens.Count];
        MyersMatchTokens(leftTokens, rightTokens, leftMatched, rightMatched);

        return (ToHighlightSpans(leftTokens, leftMatched), ToHighlightSpans(rightTokens, rightMatched));
    }

    private static IReadOnlyList<UnifiedDiffSpan> ToFullHighlightSpans(List<Token> tokens)
    {
        if (tokens.Count == 0) return Array.Empty<UnifiedDiffSpan>();
        // 合并相邻非空白 token 为一个高亮段
        var spans = new List<UnifiedDiffSpan>(tokens.Count);
        int? start = null;
        int end = 0;
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t.Text))
            {
                if (start.HasValue)
                {
                    spans.Add(new UnifiedDiffSpan(start.Value, end - start.Value));
                    start = null;
                }
                continue;
            }
            if (!start.HasValue) start = t.Start;
            end = t.Start + t.Length;
        }
        if (start.HasValue) spans.Add(new UnifiedDiffSpan(start.Value, end - start.Value));
        return spans;
    }

    private static IReadOnlyList<UnifiedDiffSpan> ToHighlightSpans(List<Token> tokens, bool[] matched)
    {
        var spans = new List<UnifiedDiffSpan>();
        int? start = null;
        int end = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var highlight = !matched[i] && !string.IsNullOrWhiteSpace(t.Text);
            if (!highlight)
            {
                if (start.HasValue)
                {
                    spans.Add(new UnifiedDiffSpan(start.Value, end - start.Value));
                    start = null;
                }
                continue;
            }
            if (!start.HasValue) start = t.Start;
            end = t.Start + t.Length;
        }
        if (start.HasValue) spans.Add(new UnifiedDiffSpan(start.Value, end - start.Value));
        return spans;
    }

    /// <summary>
    /// Myers 匹配两侧 token：产出 matched 数组。
    /// 用编辑脚本（Add/Delete 计数）反推 Equal 配对：每次 (d,k) 决策时记录走的方向，
    /// 回溯时再走一遍来定位 x/y，然后把顺势的连续 Equal 段标配对。
    /// </summary>
    private static void MyersMatchTokens(List<Token> left, List<Token> right, bool[] leftMatched, bool[] rightMatched)
    {
        var n = left.Count;
        var m = right.Count;
        var max = n + m;
        var v = new int[2 * max + 1];
        var offset = max;
        // 每个 d 保存当时的 v 和当步决策方向（goDown=+1 向左/插入，goDown=-1 向右/删除）
        var trace = new List<(int[] v, bool goDown, int k)>();
        int endK = 0;

        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                bool goDown = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]);
                if (goDown) x = v[k + 1 + offset];
                else x = v[k - 1 + offset] + 1;

                int y = x - k;
                while (x < n && y < m && string.Equals(left[x].Text, right[y].Text, StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;
                trace.Add(((int[])v.Clone(), goDown, k));

                if (x >= n && y >= m)
                {
                    endK = k;
                    goto Found;
                }
            }
        }
        Found:

        // 反向回溯：把每一步的「应该走的 prevK」转成 edit op 序列（Add/Delete）。
        // Equal 段是连续推进 x 和 y 的，没有单独记录，需要在反推时用决策方向和 v 反算。
        var ops = new List<byte>(); // 0=Add, 1=Delete
        int curK = endK;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            var (_, goDown, k) = trace[d];
            int prevK = goDown ? k + 1 : k - 1;
            ops.Add(goDown ? (byte)0 : (byte)1);
            curK = prevK;
        }
        ops.Reverse();

        // ops 中每步的语义：Delete(1) -> x++, Add(0) -> y++
        // Equal 是「没有任何 op 时顺势消耗」；反推时需要用 ops 反推 + 标记 matched。
        // 但更简单的方式：基于 trace 的 v 与 k 推出每步 prevX/prevY，进而推出本步的 x/y 增量。
        // 下面用最直接的策略：按 trace 反向走一遍，记录每步 (prevX, prevY) -> (x, y) 的差量。
        int xx = n, yy = m;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            var (vv, goDown, k) = trace[d];
            int prevK = goDown ? k + 1 : k - 1;
            int prevX = vv[prevK + offset];
            int prevY = prevX - prevK;
            // 顺势的 Equal 段
            while (xx > prevX && yy > prevY)
            {
                xx--;
                yy--;
                leftMatched[xx] = true;
                rightMatched[yy] = true;
            }
            if (d > 0)
            {
                if (goDown) yy--; // 之前是 Add
                else xx--;       // 之前是 Delete
            }
        }
        // 最后一段
        while (xx > 0 && yy > 0)
        {
            xx--;
            yy--;
            leftMatched[xx] = true;
            rightMatched[yy] = true;
        }
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
            // 跳过单空白 token，高亮时它们是噪声
            if (match.Length == 0) continue;
            list.Add(new Token(match.Value, match.Index, match.Length));
        }

        return list;
    }

    private static string ConcatDisplayText(IReadOnlyList<UnifiedDiffRow> rows)
    {
        if (rows.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(Environment.NewLine);
            sb.Append(rows[i].DisplayText);
        }
        return sb.ToString();
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
