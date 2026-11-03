using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GithubReleaseWatch;

/// <summary>把浏览器画面合成到 WPF 可视树，由外层 ScrollViewer 统一移动和裁剪。</summary>
public sealed class GitHubMarkdownView : ContentControl
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(GitHubMarkdownView),
        new PropertyMetadata(null, (d, _) => ((GitHubMarkdownView)d).Navigate()));

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    private WebView2CompositionControl? _browser;
    private bool _ready;
    private string? _documentUri;
    private static readonly Lazy<Task<CoreWebView2Environment>> EnvironmentTask = new(() =>
        CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GithubReleaseWatch", "WebView2")));

    public GitHubMarkdownView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Height = 30;
        Loaded += OnLoaded;
        Unloaded += (_, _) => DisposeBrowser();
    }

    internal void DisposeBrowser()
    {
        _ready = false;
        var browser = _browser;
        _browser = null;
        _documentUri = null;
        Content = null;
        browser?.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_browser != null) return;
        var browser = new ScrollAwareWebView2();
        _browser = browser;
        Content = browser;
        try
        {
            Trace.WriteLine("[MarkdownView] Creating environment");
            var environment = await EnvironmentTask.Value;
            if (_browser != browser) return;
            await browser.EnsureCoreWebView2Async(environment);
            Trace.WriteLine("[MarkdownView] Browser initialized");
            if (_browser != browser) return;
            browser.CoreWebView2.WebMessageReceived += (_, message) =>
            {
                if (_browser == browser) OnMessage(message);
            };
            browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (args.Uri == "about:blank" || args.Uri == _documentUri) return;
                args.Cancel = true;
                OpenLink(args.Uri);
            };
            browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenLink(args.Uri);
            };
            // 每次文档加载后测量实际内容。图片、宽度变化都会重新报告高度。
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                window.addEventListener('DOMContentLoaded', () => {
                    const content = document.querySelector('.markdown-body');
                    if (!content) return;
                    let last = 0;
                    const report = () => {
                        const height = Math.ceil(content.getBoundingClientRect().height + 16);
                        if (height !== last) {
                            last = height;
                            chrome.webview.postMessage({type:'height', value:height});
                        }
                    };
                    new ResizeObserver(report).observe(content);
                    report();
                });
                """);
            if (_browser != browser) return;
            _ready = true;
            Trace.WriteLine("[MarkdownView] Navigating to HTML");
            Navigate();
        }
        catch (Exception ex)
        {
            if (_browser != browser) return;
            _ready = false;
            Trace.WriteLine($"[MarkdownView] {ex}");
            Height = 60;
            Content = new TextBlock
            {
                Text = "版本说明加载失败，请确认已安装 Microsoft Edge WebView2 Runtime。\n" + ex.Message,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private void Navigate()
    {
        if (_ready && _browser != null)
        {
            string html = Html ?? GitHubHtmlPage.Wrap(null);
            // 组合控件的 NavigateToString 在内部使用 data URL。
            // 只允许本次由应用生成的文档，普通链接仍交给系统浏览器。
            _documentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            _browser.NavigateToString(html);
        }
    }

    private void OnMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != "about:blank" && e.Source != _documentUri) return;
        if (!MarkdownMessage.TryParse(e.WebMessageAsJson, out var message)) return;
        if (message.Type == "height") Height = Math.Max(30, message.Value);
        else if (message.Type == "wheel" && Window.GetWindow(this) is MainWindow window)
            window.WheelScrollByRawDelta((int)message.Value);
    }

    private static void OpenLink(string value)
    {
        ExternalLinks.TryOpen(value, out _);
    }
}
