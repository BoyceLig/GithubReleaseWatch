using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using static GithubReleaseWatch.Tests.Program;

namespace GithubReleaseWatch.Tests;

/// <summary>可选真实键盘检查：外部按 Ctrl+C，由剪贴板结果判断是否复制成功。</summary>
internal static class BrowserInputCheck
{
    internal static async Task RunAsync()
    {
        await TestAsync("Physical Ctrl+C copies selected browser text after scrolling", async () =>
        {
            const string expected = "GitHub Markdown native copy verification";
            var clipboard = Clipboard.GetDataObject();
            var previousClipboard = new DataObject();
            if (clipboard != null)
                foreach (string format in clipboard.GetFormats(false))
                    if (clipboard.GetData(format, false) is { } data) previousClipboard.SetData(format, data);
            var view = new GitHubMarkdownView
            {
                Html = GitHubHtmlPage.Wrap(string.Concat(Enumerable.Range(0, 100).Select(i =>
                    $"<p id='line{i}'>{(i == 25 ? expected : "Selectable line " + i)}</p>")))
            };
            var scroll = new BrowserScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var window = new Window { Title = "Markdown Ctrl+C verification", Width = 850, Height = 600, Content = scroll };
            try
            {
                window.Show();
                for (int i = 0; i < 150 && view.Height < 1000; i++) await Task.Delay(100);
                Check(view.Height > 1000, "input page failed to load");
                var browser = (ScrollAwareWebView2)view.Content!;
                window.Activate();
                browser.Focus();
                await Task.Delay(100);
                scroll.ScrollToVerticalOffset(500);
                await Task.Delay(200);
                await browser.ExecuteScriptAsync("""
                    const range = document.createRange();
                    range.selectNodeContents(document.getElementById('line25'));
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    """);
                Console.WriteLine("INPUT READY: press Ctrl+C in Markdown Ctrl+C verification");
                for (int i = 0; i < 600; i++)
                {
                    await Task.Delay(100);
                    try
                    {
                        if (Clipboard.ContainsText() && Clipboard.GetText() == expected)
                        {
                            Console.WriteLine("INPUT COPIED: browser selection reached clipboard");
                            return;
                        }
                    }
                    catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401d0) { }
                }
                throw new TimeoutException("Selected browser text did not reach the clipboard");
            }
            finally
            {
                window.Close();
                // Chromium 写剪贴板是异步的，关闭控件后等释放，再恢复已物化的原内容。
                for (int attempt = 0; ; attempt++)
                {
                    await Task.Delay(100);
                    try { Clipboard.SetDataObject(previousClipboard, true); break; }
                    catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401d0 && attempt < 9) { }
                }
            }
        });
    }
}
