using GitX.Core.Models;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows.Media;

namespace GitX.Views;

/// <summary>
/// 统一 diff 的行高亮器：整行底色 + 行内词级高亮。
/// </summary>
public sealed class UnifiedDiffHighlighter : DocumentColorizingTransformer
{
    private readonly Func<IReadOnlyList<UnifiedDiffRow>> _rowsProvider;
    private readonly Brush _prefixNeutralBrush;
    private readonly Brush _prefixAddedBrush;
    private readonly Brush _prefixDeletedBrush;
    private readonly Brush _addedLineBrush;
    private readonly Brush _deletedLineBrush;
    private readonly Brush _inlineAddedBrush;
    private readonly Brush _inlineDeletedBrush;

    public UnifiedDiffHighlighter(Func<IReadOnlyList<UnifiedDiffRow>> rowsProvider)
    {
        _rowsProvider = rowsProvider;
        _prefixNeutralBrush = FreezeBrush(Color.FromArgb(0x28, 0x2D, 0x29, 0x24));
        _prefixAddedBrush = FreezeBrush(Color.FromArgb(0x44, 0x26, 0x30, 0x26));
        _prefixDeletedBrush = FreezeBrush(Color.FromArgb(0x44, 0x32, 0x23, 0x21));
        _addedLineBrush = FreezeBrush(Color.FromArgb(0x22, 0xA6, 0xE3, 0xA1));
        _deletedLineBrush = FreezeBrush(Color.FromArgb(0x22, 0xF3, 0x8B, 0xA8));
        _inlineAddedBrush = FreezeBrush(Color.FromArgb(0x55, 0xA6, 0xE3, 0xA1));
        _inlineDeletedBrush = FreezeBrush(Color.FromArgb(0x55, 0xF3, 0x8B, 0xA8));
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var rows = _rowsProvider();
        if (rows == null || rows.Count == 0) return;

        var index = line.LineNumber - 1;
        if (index < 0 || index >= rows.Count) return;

        var row = rows[index];
        if (row.DisplayText == "...")
        {
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(Brushes.DimGray);
            });
            return;
        }

        if (row.Kind == UnifiedDiffRowKind.Added)
        {
            ColorizeWholeLine(line, _addedLineBrush);
            ColorizePrefix(line, row);
            ColorizeInlineSpans(line, row, _inlineAddedBrush);
        }
        else if (row.Kind == UnifiedDiffRowKind.Deleted)
        {
            ColorizeWholeLine(line, _deletedLineBrush);
            ColorizePrefix(line, row);
            ColorizeInlineSpans(line, row, _inlineDeletedBrush);
        }
        else
        {
            ColorizePrefix(line, row);
        }
    }

    private void ColorizePrefix(DocumentLine line, UnifiedDiffRow row)
    {
        if (row.ContentOffset <= 0) return;

        var endOffset = Math.Min(line.EndOffset, line.Offset + row.ContentOffset);
        if (endOffset <= line.Offset) return;

        var brush = row.Kind switch
        {
            UnifiedDiffRowKind.Added => _prefixAddedBrush,
            UnifiedDiffRowKind.Deleted => _prefixDeletedBrush,
            _ => _prefixNeutralBrush
        };

        ChangeLinePart(line.Offset, endOffset, element =>
        {
            element.TextRunProperties.SetBackgroundBrush(brush);
            element.TextRunProperties.SetForegroundBrush(Brushes.LightGray);
        });
    }

    private void ColorizeWholeLine(DocumentLine line, Brush background)
    {
        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.TextRunProperties.SetBackgroundBrush(background);
        });
    }

    private void ColorizeInlineSpans(DocumentLine line, UnifiedDiffRow row, Brush background)
    {
        foreach (var span in row.InlineSpans)
        {
            var start = line.Offset + row.ContentOffset + span.Start;
            var end = start + span.Length;
            if (end <= start) continue;

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(background);
            });
        }
    }

    private static Brush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
