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
        return IsUnorderedListLine(line) || OrderedListRegex.IsMatch(line);
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
