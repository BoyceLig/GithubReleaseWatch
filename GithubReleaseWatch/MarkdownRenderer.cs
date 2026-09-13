using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

// 消除 csproj 引入 UseWindowsForms 后的命名冲突
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 极简 Markdown → FlowDocument 转换器。覆盖常见语法：
    ///   # / ## / ### 标题
    ///   **粗体** / *斜体* / `行内代码`
    ///   ``` 代码块 ```
    ///   - 列表项 / 1. 有序列表
    ///   [文字](url) 链接
    ///   > 引用
    ///
    /// 不追求 GFM 完整覆盖（GitHub Release Notes 一般只有上述语法），
    /// 遇到未识别的语法回退为纯文本段落，保证不丢内容。
    /// </summary>
    internal static class MarkdownRenderer
    {
        private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex UlItemRx = new(@"^[\-\*]\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex OlItemRx = new(@"^(\d+)\.\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex BlockquoteRx = new(@"^>\s?(.*)$", RegexOptions.Compiled);
        // 裸 URL 自动链接（GitHub Release Notes 里非常常见）
        private static readonly Regex BareUrlRx = new(@"https?://[^\s<>""\)\]]+", RegexOptions.Compiled);
        // HTML 注释（含跨行）：GitHub 上常见的 <!-- fclash:changelog:begin --> 之类标记，
        // 有些仓库甚至把整段 JSON 塞在注释里，必须整体剔除。
        private static readonly Regex HtmlCommentRx = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        // 其它常见 HTML 标签（<div>, <br>, <img>, <a>, <sub> 等），成对或自闭合都去掉
        private static readonly Regex HtmlTagRx = new(@"</?[a-zA-Z][a-zA-Z0-9]*(?:\s[^<>]*)?/?>", RegexOptions.Compiled);
        // Markdown 图片 ![alt](src) → 换成链接形式（WPF FlowDocument 不便内嵌远程图片）
        private static readonly Regex MdImageRx = new(@"!\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
        // 徽章式嵌套链接 [![alt](imgUrl)](linkUrl) → 直接取外层链接，避免产生嵌套 Hyperlink
        private static readonly Regex BadgeLinkRx = new(
            @"\[!\[[^\]]*\]\([^)]*\)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
        // GFM 表格：表头行（含 | 分隔的列名）
        private static readonly Regex TableHeaderRx = new(@"^\|.+\|\s*$", RegexOptions.Compiled);
        // 表格分隔行（|---|---| 格式，可含 : 左右对齐标记）
        private static readonly Regex TableSeparatorRx = new(@"^\|[\s\:\-]+\|\s*$", RegexOptions.Compiled);

        public static FlowDocument Render(string? markdown)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 13,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#374151")!,
                LineHeight = 20,
            };
            // FlowDocument 只能属于一个 Parent（FlowDocumentScrollViewer / RichTextBox），
            // 同一实例多次绑定会抛 "Specified element is already the logical child"。
            // 解决：每次调用 Render 都 new 一个新实例（开销很小），并且 ItemContainer
            // 不可回收时由 GC 释放（无强引用）。
            // 注意：绑定应使用 Mode=OneWay，并在 ReleaseInfo 不可变的前提下反复读取。

            if (string.IsNullOrWhiteSpace(markdown))
            {
                doc.Blocks.Add(new Paragraph(new Run("(无版本说明)")) { Foreground = Brushes.Gray });
                return doc;
            }

            var lines = Preprocess(markdown).Replace("\r\n", "\n").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];

                // 代码块 ```
                if (line.TrimStart().StartsWith("```"))
                {
                    var codeLines = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++; // 跳过的 ```
                    doc.Blocks.Add(BuildCodeBlock(string.Join("\n", codeLines)));
                    continue;
                }

                // GFM 表格（| 列1 | 列2 | + 分隔行 + 数据行）
                if (TableHeaderRx.IsMatch(line) && i + 1 < lines.Length && TableSeparatorRx.IsMatch(lines[i + 1]))
                {
                    var table = ParseTable(lines, ref i);
                    if (table != null) doc.Blocks.Add(table);
                    continue;
                }

                // 标题
                var hMatch = HeadingRx.Match(line);
                if (hMatch.Success)
                {
                    int level = hMatch.Groups[1].Value.Length;
                    doc.Blocks.Add(BuildHeading(level, hMatch.Groups[2].Value));
                    i++;
                    continue;
                }

                // 引用
                if (BlockquoteRx.IsMatch(line))
                {
                    var quoteLines = new List<string>();
                    while (i < lines.Length && BlockquoteRx.IsMatch(lines[i]))
                    {
                        quoteLines.Add(BlockquoteRx.Match(lines[i]).Groups[1].Value);
                        i++;
                    }
                    doc.Blocks.Add(BuildBlockquote(string.Join("\n", quoteLines)));
                    continue;
                }

                // 无序列表（支持续行：缩进且非列表标记的行并入上一项）
                if (UlItemRx.IsMatch(line))
                {
                    var list = new List();
                    while (i < lines.Length && UlItemRx.IsMatch(lines[i]))
                    {
                        var m = UlItemRx.Match(lines[i]);
                        var itemText = m.Groups[1].Value;
                        i++;
                        // 吸收续行（缩进 2+ 空格 / Tab 且不是新的列表项或标题）
                        var cont = new List<string>();
                        while (i < lines.Length && IsContinuation(lines[i]))
                        {
                            cont.Add(lines[i].Trim());
                            i++;
                        }
                        if (cont.Count > 0) itemText += " " + string.Join(" ", cont);
                        list.ListItems.Add(new ListItem(new Paragraph(BuildInlines(itemText))));
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                // 有序列表
                if (OlItemRx.IsMatch(line))
                {
                    var list = new List { MarkerStyle = TextMarkerStyle.Decimal };
                    while (i < lines.Length && OlItemRx.IsMatch(lines[i]))
                    {
                        var m = OlItemRx.Match(lines[i]);
                        var itemText = m.Groups[2].Value;
                        i++;
                        var cont = new List<string>();
                        while (i < lines.Length && IsContinuation(lines[i]))
                        {
                            cont.Add(lines[i].Trim());
                            i++;
                        }
                        if (cont.Count > 0) itemText += " " + string.Join(" ", cont);
                        list.ListItems.Add(new ListItem(new Paragraph(BuildInlines(itemText))));
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                // 空行
                if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

                // 段落（连续非空行视为同一段落）
                var paraLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                       && !HeadingRx.IsMatch(lines[i])
                       && !UlItemRx.IsMatch(lines[i])
                       && !OlItemRx.IsMatch(lines[i])
                       && !BlockquoteRx.IsMatch(lines[i])
                       && !lines[i].TrimStart().StartsWith("```"))
                {
                    paraLines.Add(lines[i]);
                    i++;
                }
                if (paraLines.Count > 0)
                {
                    doc.Blocks.Add(new Paragraph(BuildInlines(string.Join(" ", paraLines))));
                }
            }

            return doc;
        }

        // ===== 段落构建 =====

        /// <summary>
        /// 判断是否为列表项的续行：有缩进（2+ 空格或 Tab）且本身不是新的列表项/标题/引用/代码块。
        /// GitHub Release Notes 里长条目常折行书写，需要并入同一项。
        /// </summary>
        private static bool IsContinuation(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (!(line.StartsWith("  ") || line.StartsWith('\t'))) return false;
            var t = line.TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ")) return false;
            if (OlItemRx.IsMatch(t)) return false;
            if (HeadingRx.IsMatch(t)) return false;
            if (BlockquoteRx.IsMatch(t)) return false;
            if (t.StartsWith("```")) return false;
            return true;
        }

        /// <summary>
        /// 预处理：剔除 Markdown 里对纯文本渲染器无关或有干扰的内容。
        ///   - HTML 注释（含跨行，GitHub 上仓库常用来塞 changelog 标记 / JSON）
        ///   - 其它 HTML 标签
        ///   - 图片语法 → 链接形式（避免破图占位）
        ///   - 行尾空格、BOM
        /// </summary>
        private static string Preprocess(string markdown)
        {
            var s = markdown;

            // 去掉 BOM
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);

            // HTML 注释（跨行）—— 整体剔除
            s = HtmlCommentRx.Replace(s, "");

            // 先处理徽章式嵌套链接 [![alt](img)](link)：只保留外层链接文字/地址，
            // 否则后续单图替换会生成 [[alt](img)](link) 这种嵌套结构。
            s = BadgeLinkRx.Replace(s, m =>
            {
                var link = m.Groups[1].Value.Trim();
                var seg = link.TrimEnd('/').Split('/');
                return $"[{ (seg.Length > 0 ? seg[^1] : link) }]({link})";
            });

            // 图片 ![alt](src) → [alt](src)；alt 为空时用 src 的末段做文字
            s = MdImageRx.Replace(s, m =>
            {
                var alt = m.Groups[1].Value.Trim();
                var src = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(alt))
                {
                    var seg = src.TrimEnd('/').Split('/');
                    alt = seg.Length > 0 ? seg[^1] : src;
                }
                return $"[{alt}]({src})";
            });

            // 其它 HTML 标签
            s = HtmlTagRx.Replace(s, "");

            return s;
        }

        private static Paragraph BuildHeading(int level, string text)
        {
            double size = level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                4 => 14,
                _ => 13,
            };
            return new Paragraph(BuildInlines(text.Trim()))
            {
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#111827")!,
                Margin = new Thickness(0, 10, 0, 4),
            };
        }

        private static Paragraph BuildBlockquote(string text)
        {
            var p = new Paragraph(BuildInlines(text))
            {
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#6B7280")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#D1D5DB")!,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 0, 0, 0),
                Margin = new Thickness(0, 4, 0, 4),
            };
            return p;
        }

        private static Paragraph BuildCodeBlock(string code)
        {
            var p = new Paragraph
            {
                Background = (Brush)new BrushConverter().ConvertFromString("#F3F4F6")!,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4),
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                FontSize = 12,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E5E7EB")!,
                BorderThickness = new Thickness(1),
            };
            p.Inlines.Add(new Run(code)
            {
                Foreground = (Brush)new BrushConverter().ConvertFromString("#1F2937")!,
            });
            return p;
        }

        /// <summary>
        /// 解析 GFM 表格：从表头行开始，读取分隔行和所有后续数据行，
        /// 构建一个 WPF Table。调用时 i 指向表头行，返回后 i 指向最后一个数据行之后。
        /// </summary>
        private static Table? ParseTable(string[] lines, ref int i)
        {
            // 解析表头
            var headers = SplitTableRow(lines[i]);
            i++; // 跳过表头

            // 跳过分隔行 (|---|---|)
            if (i < lines.Length && TableSeparatorRx.IsMatch(lines[i])) i++;

            // 读取数据行（直到遇到空行或非表格行）
            var dataRows = new List<string[]>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].StartsWith("|"))
            {
                dataRows.Add(SplitTableRow(lines[i]));
                i++;
            }

            if (headers.Length == 0) return null;

            int colCount = headers.Length;
            var table = new Table
            {
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#D1D5DB")!,
                BorderThickness = new Thickness(1),
                CellSpacing = 0,
                Background = Brushes.White,
                Margin = new Thickness(0, 8, 0, 8),
            };

            // 表头列定义 + 表头行
            var headerRowGroup = new TableRowGroup();
            var headerRow = new TableRow { Background = (Brush)new BrushConverter().ConvertFromString("#F9FAFB")! };
            for (int c = 0; c < colCount; c++)
            {
                table.Columns.Add(new TableColumn { Width = GridLength.Auto });
                var para = new Paragraph(BuildInlines(c < headers.Length ? headers[c] : ""))
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#374151")!,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0),
                };
                if (c < colCount - 1)
                    para.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E5E7EB")!;
                para.BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1);
                headerRow.Cells.Add(new TableCell { Blocks = { para } });
            }
            headerRowGroup.Rows.Add(headerRow);
            table.RowGroups.Add(headerRowGroup);

            // 数据行
            var bodyRowGroup = new TableRowGroup();
            foreach (var row in dataRows)
            {
                var tr = new TableRow();
                for (int c = 0; c < Math.Max(colCount, row.Length); c++)
                {
                    var text = c < row.Length ? row[c] : "";
                    var para = new Paragraph(BuildInlines(text))
                    {
                        FontSize = 12,
                        Foreground = (Brush)new BrushConverter().ConvertFromString("#374151")!,
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(0),
                    };
                    if (c < colCount - 1)
                        para.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E5E7EB")!;
                    para.BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1);
                    tr.Cells.Add(new TableCell { Blocks = { para } });
                }
                bodyRowGroup.Rows.Add(tr);
            }
            table.RowGroups.Add(bodyRowGroup);

            return table;
        }

        /// <summary>把 | col1 | col2 | 行拆分为单元格数组（去掉首尾 | 和空白）。</summary>
        private static string[] SplitTableRow(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1).TrimStart();
            if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            return trimmed.Split('|', StringSplitOptions.TrimEntries);
        }

        // ===== 行内解析 =====

        private static Inline BuildInlines(string text)
        {
            var container = new Span();

            int i = 0;
            while (i < text.Length)
            {
                // [文字](url)
                if (text[i] == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    int parenStart = closeBracket > 0 ? text.IndexOf('(', closeBracket) : -1;
                    int parenEnd = parenStart > 0 ? text.IndexOf(')', parenStart) : -1;
                    if (closeBracket > 0 && parenStart == closeBracket + 1 && parenEnd > parenStart)
                    {
                        var linkText = text.Substring(i + 1, closeBracket - i - 1);
                        var url = text.Substring(parenStart + 1, parenEnd - parenStart - 1);
                        try
                        {
                            var link = new Hyperlink(new Run(linkText))
                            {
                                NavigateUri = new Uri(url, UriKind.Absolute),
                                Foreground = (Brush)new BrushConverter().ConvertFromString("#2563EB")!,
                            };
                            container.Inlines.Add(link);
                        }
                        catch (UriFormatException)
                        {
                            // URL 非法（相对路径/格式错误）→ 回退为 [text](url) 纯文本
                            container.Inlines.Add(new Run($"[{linkText}]({url})"));
                        }
                        i = parenEnd + 1;
                        continue;
                    }
                }

                // **粗体**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2);
                    if (end > i + 2)
                    {
                        container.Inlines.Add(new Run(text.Substring(i + 2, end - i - 2))
                        {
                            FontWeight = FontWeights.Bold,
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // *斜体*
                if (text[i] == '*')
                {
                    int end = text.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        container.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontStyle = FontStyles.Italic,
                        });
                        i = end + 1;
                        continue;
                    }
                }

                // `行内代码`
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        container.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                            Background = (Brush)new BrushConverter().ConvertFromString("#F3F4F6")!,
                            Foreground = (Brush)new BrushConverter().ConvertFromString("#DC2626")!,
                        });
                        i = end + 1;
                        continue;
                    }
                }

                // 普通字符 —— 累积到下一个特殊符号
                int next = text.Length;
                foreach (char sig in new[] { '[', '*', '`', 'h' })
                {
                    int idx = text.IndexOf(sig, i + 1);
                    if (idx > 0 && idx < next) next = idx;
                }

                // 检查这段普通文字里是否有裸 URL（以 h 起始的候选点）
                var plain = text.Substring(i, next - i);
                AppendWithBareUrls(container, plain);
                i = next;
            }

            return container;
        }

        /// <summary>把一段普通文本按裸 URL 切分，URL 部分转成可点击链接。</summary>
        private static void AppendWithBareUrls(Span container, string text)
        {
            int pos = 0;
            foreach (Match m in BareUrlRx.Matches(text))
            {
                if (m.Index > pos)
                    container.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));

                var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                try
                {
                    container.Inlines.Add(new Hyperlink(new Run(url))
                    {
                        NavigateUri = new Uri(url, UriKind.Absolute),
                        Foreground = (Brush)new BrushConverter().ConvertFromString("#2563EB")!,
                    });
                }
                catch
                {
                    container.Inlines.Add(new Run(url)); // Uri 非法则回退纯文本
                }

                // 被 Trim 掉的尾部标点补回纯文本
                if (url.Length < m.Value.Length)
                    container.Inlines.Add(new Run(m.Value.Substring(url.Length)));

                pos = m.Index + m.Value.Length;
            }

            if (pos < text.Length)
                container.Inlines.Add(new Run(text.Substring(pos)));
        }
    }
}