using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace XCL2.App.Views;

/// <summary>
/// 轻量 Markdown 查看器。专门用于 AI 回复，不引入额外 NuGet 包，支持标题、粗体、斜体、
/// 删除线、行内代码、代码块、无序/有序列表、引用、分隔线与链接。
/// </summary>
public sealed class MarkdownViewer : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    private static readonly Regex InlineTokenRegex = new(
        @"(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|~~[^~]+~~|\[[^\]]+\]\([^\)]+\)|\*[^*\r\n]+\*|_[^_\r\n]+_)",
        RegexOptions.Compiled);

    private static readonly Regex OrderedListRegex = new(@"^\s*(\d+)[\.)]\s+(.+)$", RegexOptions.Compiled);

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownViewer()
    {
        Orientation = Orientation.Vertical;
        SnapsToDevicePixels = true;
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
            viewer.RenderMarkdown(e.NewValue as string ?? string.Empty);
    }

    private void RenderMarkdown(string markdown)
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // ```lang ... ```
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    codeLines.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddCodeBlock(string.Join(Environment.NewLine, codeLines));
                continue;
            }

            // # 标题
            int headingLevel = GetHeadingLevel(trimmed, out var headingText);
            if (headingLevel > 0)
            {
                var size = headingLevel switch
                {
                    1 => 20d,
                    2 => 18d,
                    3 => 16d,
                    4 => 15d,
                    _ => 14d
                };
                var block = CreateTextBlock(size, FontWeights.SemiBold, new Thickness(0, 7, 0, 3));
                AddInlineMarkdown(block, headingText);
                Children.Add(block);
                i++;
                continue;
            }

            // --- / *** 分隔线
            if (trimmed is "---" or "***" or "___")
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 7, 0, 7) };
                line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
                Children.Add(line);
                i++;
                continue;
            }

            // > 引用
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quoteText = trimmed[1..].TrimStart();
                var text = CreateTextBlock(12.5, FontWeights.Normal, new Thickness(0));
                text.FontStyle = FontStyles.Italic;
                AddInlineMarkdown(text, quoteText);
                var quote = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 3, 4, 3),
                    Margin = new Thickness(0, 4, 0, 4),
                    Child = text
                };
                quote.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                quote.SetResourceReference(Border.BackgroundProperty, "SideBrush");
                Children.Add(quote);
                i++;
                continue;
            }

            // - / * / + 无序列表
            if (IsUnorderedListLine(raw))
            {
                while (i < lines.Length && IsUnorderedListLine(lines[i]))
                {
                    var content = lines[i].TrimStart()[2..].TrimStart();
                    AddListRow("•", content);
                    i++;
                }
                continue;
            }

            // 1. / 1) 有序列表
            var orderedMatch = OrderedListRegex.Match(raw);
            if (orderedMatch.Success)
            {
                while (i < lines.Length)
                {
                    var match = OrderedListRegex.Match(lines[i]);
                    if (!match.Success) break;
                    AddListRow(match.Groups[1].Value + ".", match.Groups[2].Value);
                    i++;
                }
                continue;
            }

            // | 表头 | ... |
            // |------|-----|
            // | 单元格 | ... |
            // 修复"AI 助手回复里用 Markdown 表格格式时不会正常显示"：以前这里完全没有表格分支，
            // 表格的每一行都会落进下面的"普通段落"分支，原样把 | 字符打印出来。
            // GFM 表格的判定标准是"表头行 + 紧跟着的分隔行（只含 -/:/| 和空白）"，只看这两行就够，
            // 不用管后面数据行是否对齐——对齐交给下面的 Grid 布局自动处理。
            if (IsTableRow(raw) && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1]))
            {
                var headerCells = ParseTableRow(raw);
                i += 2;
                var dataRows = new List<string[]>();
                while (i < lines.Length && IsTableRow(lines[i]))
                {
                    dataRows.Add(ParseTableRow(lines[i]));
                    i++;
                }
                AddTable(headerCells, dataRows);
                continue;
            }

            // 普通段落：连续普通行保留软换行，但不显示 Markdown 标记本身。
            var paragraphLines = new List<string>();
            while (i < lines.Length && !IsBlockStart(lines[i]))
                paragraphLines.Add(lines[i++]);
            if (paragraphLines.Count == 0)
            {
                paragraphLines.Add(lines[i++]);
            }

            var paragraph = CreateTextBlock(13, FontWeights.Normal, new Thickness(0, 2, 0, 5));
            for (int lineIndex = 0; lineIndex < paragraphLines.Count; lineIndex++)
            {
                AddInlineMarkdown(paragraph, paragraphLines[lineIndex]);
                if (lineIndex < paragraphLines.Count - 1)
                    paragraph.Inlines.Add(new LineBreak());
            }
            Children.Add(paragraph);
        }
    }

    private static bool IsBlockStart(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith(">", StringComparison.Ordinal))
            return true;
        if (GetHeadingLevel(t, out _) > 0 || t is "---" or "***" or "___")
            return true;
        // 段落累积到表格行时也要停下，不然表格第一行会被前一段普通文字的软换行吞掉。
        if (IsTableRow(line))
            return true;
        return IsUnorderedListLine(line) || OrderedListRegex.IsMatch(line);
    }

    /// <summary>是不是"看起来像表格行"：含至少一个未转义的 `|`。用来判断段落到哪里为止，
    /// 以及表头/数据行的扫描范围；真正确认"这是表格"还要看下一行是不是分隔行，见调用处。</summary>
    private static bool IsTableRow(string line) => CountUnescapedPipes(line.Trim()) >= 1;

    /// <summary>分隔行：`|---|:---:|---:|` 这种只含 `-`、`:`、`|` 和空白的行（且至少有一个 `-`）。</summary>
    private static bool IsTableSeparatorRow(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && t.Contains('-') && TableSeparatorRegex.IsMatch(t);
    }

    private static readonly Regex TableSeparatorRegex = new(
        @"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$", RegexOptions.Compiled);

    private static int CountUnescapedPipes(string t)
    {
        int count = 0;
        for (int idx = 0; idx < t.Length; idx++)
        {
            if (t[idx] == '\\' && idx + 1 < t.Length && t[idx + 1] == '|') { idx++; continue; }
            if (t[idx] == '|') count++;
        }
        return count;
    }

    /// <summary>把一行表格行拆成单元格：去掉首尾的 `|`，按未转义的 `|` 分割，`\|` 表示字面竖线。</summary>
    private static string[] ParseTableRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal)) t = t[1..];
        if (t.EndsWith("|", StringComparison.Ordinal) && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];

        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (int idx = 0; idx < t.Length; idx++)
        {
            if (t[idx] == '\\' && idx + 1 < t.Length && t[idx + 1] == '|')
            {
                sb.Append('|');
                idx++;
            }
            else if (t[idx] == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(t[idx]);
        }
        cells.Add(sb.ToString().Trim());
        return cells.ToArray();
    }

    private static bool IsUnorderedListLine(string line)
    {
        var t = line.TrimStart();
        return t.Length > 2 && (t.StartsWith("- ", StringComparison.Ordinal) ||
                                t.StartsWith("* ", StringComparison.Ordinal) ||
                                t.StartsWith("+ ", StringComparison.Ordinal));
    }

    private static int GetHeadingLevel(string trimmed, out string text)
    {
        int count = 0;
        while (count < trimmed.Length && count < 6 && trimmed[count] == '#') count++;
        if (count > 0 && count < trimmed.Length && char.IsWhiteSpace(trimmed[count]))
        {
            text = trimmed[(count + 1)..].Trim();
            return count;
        }
        text = string.Empty;
        return 0;
    }

    private TextBlock CreateTextBlock(double fontSize, FontWeight weight, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Max(fontSize * 1.45, 18),
            Margin = margin
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return tb;
    }

    private void AddListRow(string marker, string content)
    {
        var grid = new Grid { Margin = new Thickness(3, 1, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var markerBlock = CreateTextBlock(12.5, FontWeights.SemiBold, new Thickness(0, 0, 7, 0));
        markerBlock.Text = marker;
        markerBlock.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var contentBlock = CreateTextBlock(13, FontWeights.Normal, new Thickness(0));
        AddInlineMarkdown(contentBlock, content);
        Grid.SetColumn(contentBlock, 1);
        grid.Children.Add(markerBlock);
        grid.Children.Add(contentBlock);
        Children.Add(grid);
    }

    /// <summary>用 Grid 画一个简单的带边框表格：表头行加粗、浅底色，其余行只画分隔线。
    /// 列数取表头和所有数据行里最宽的那一行，缺的单元格补空——AI 输出的表格经常某一行少写
    /// 一两个 `|`，这里不因为某行列数不一致就整段放弃渲染。</summary>
    private void AddTable(string[] headerCells, List<string[]> dataRows)
    {
        int colCount = headerCells.Length;
        foreach (var row in dataRows) colCount = Math.Max(colCount, row.Length);
        if (colCount == 0) return;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void AddRow(string[] cells, bool isHeader, int rowIndex)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < colCount; c++)
            {
                var text = c < cells.Length ? cells[c] : string.Empty;
                var cellText = CreateTextBlock(12.5, isHeader ? FontWeights.SemiBold : FontWeights.Normal, new Thickness(0));
                AddInlineMarkdown(cellText, text);

                var border = new Border
                {
                    BorderThickness = new Thickness(0, 0, c == colCount - 1 ? 0 : 1, 1),
                    Padding = new Thickness(9, 6, 9, 6),
                    Child = cellText
                };
                border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
                if (isHeader) border.SetResourceReference(Border.BackgroundProperty, "SideBrush");

                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        AddRow(headerCells, true, 0);
        for (int r = 0; r < dataRows.Count; r++)
            AddRow(dataRows[r], false, r + 1);

        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 3, 0, 7),
            Child = grid
        };
        outer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Children.Add(outer);
    }

    private void AddCodeBlock(string code)
    {
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            IsTabStop = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8, 7, 8, 7)
        };
        box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");

        var border = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 5, 0, 7),
            Child = box
        };
        border.SetResourceReference(Border.BackgroundProperty, "SideBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        Children.Add(border);
    }

    private static void AddInlineMarkdown(TextBlock target, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        int cursor = 0;
        foreach (Match match in InlineTokenRegex.Matches(text))
        {
            if (match.Index > cursor)
                target.Inlines.Add(new Run(UnescapeMarkdown(text[cursor..match.Index])));

            var token = match.Value;
            if (token.StartsWith("`", StringComparison.Ordinal) && token.EndsWith("`", StringComparison.Ordinal))
            {
                var run = new Run(token[1..^1]) { FontFamily = new FontFamily("Consolas") };
                run.SetResourceReference(TextElement.BackgroundProperty, "SideBrush");
                target.Inlines.Add(run);
            }
            else if ((token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal)) ||
                     (token.StartsWith("__", StringComparison.Ordinal) && token.EndsWith("__", StringComparison.Ordinal)))
            {
                target.Inlines.Add(new Bold(new Run(UnescapeMarkdown(token[2..^2]))));
            }
            else if (token.StartsWith("~~", StringComparison.Ordinal) && token.EndsWith("~~", StringComparison.Ordinal))
            {
                target.Inlines.Add(new Run(UnescapeMarkdown(token[2..^2])) { TextDecorations = TextDecorations.Strikethrough });
            }
            else if (token.StartsWith("[", StringComparison.Ordinal))
            {
                int split = token.IndexOf("](", StringComparison.Ordinal);
                if (split > 1)
                {
                    var label = token[1..split];
                    var url = token[(split + 2)..^1];
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        var link = new Hyperlink(new Run(label)) { NavigateUri = uri, ToolTip = url };
                        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                        link.RequestNavigate += OpenLink;
                        target.Inlines.Add(link);
                    }
                    else
                    {
                        target.Inlines.Add(new Run(label));
                    }
                }
                else target.Inlines.Add(new Run(token));
            }
            else if ((token.StartsWith("*", StringComparison.Ordinal) && token.EndsWith("*", StringComparison.Ordinal)) ||
                     (token.StartsWith("_", StringComparison.Ordinal) && token.EndsWith("_", StringComparison.Ordinal)))
            {
                target.Inlines.Add(new Italic(new Run(UnescapeMarkdown(token[1..^1]))));
            }
            else
            {
                target.Inlines.Add(new Run(UnescapeMarkdown(token)));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
            target.Inlines.Add(new Run(UnescapeMarkdown(text[cursor..])));
    }

    private static string UnescapeMarkdown(string text) => text
        .Replace("\\*", "*")
        .Replace("\\_", "_")
        .Replace("\\#", "#")
        .Replace("\\`", "`")
        .Replace("\\[", "[")
        .Replace("\\]", "]");

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // 链接打不开不影响聊天渲染。
        }
    }
}
