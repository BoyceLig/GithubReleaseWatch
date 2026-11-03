using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using System.Reflection;
using System.Windows.Input;
using static GithubReleaseWatch.Tests.Program;

namespace GithubReleaseWatch.Tests;

internal static class BrowserChecks
{
    internal static async Task RunAsync()
    {
        await TestAsync("Composition browser: long notes, resize, rapid scroll, shrink, unload/reload", async () =>
        {
            var view = new GitHubMarkdownView
            {
                Html = GitHubHtmlPage.Wrap(string.Concat(Enumerable.Range(0, 100).Select(i =>
                    $"<p>Line {i}: <strong>GitHub HTML</strong> <code>code</code> 中文测试 " +
                    "A release note with enough text to wrap when the window gets narrower, preserving GitHub styles.</p>")))
            };
            var panel = new StackPanel(); panel.Children.Add(view);
            var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var window = new Window { Title = "GithubReleaseWatch browser regression", Width = 950, Height = 600, Content = scroll };
            try
            {
                window.Show();
                await WaitUntilAsync(() => view.Height > 2000, "initial HTML height");
                double initial = view.Height;
                window.Width = 500;
                await WaitUntilAsync(() => view.Height > initial, "height after resize");
                for (int i = 0; i < 60; i++)
                {
                    scroll.ScrollToVerticalOffset(i % 2 == 0 ? scroll.ScrollableHeight : 0);
                    await Task.Delay(16);
                }
                Check(scroll.ScrollableHeight > 0, "scroll extent");
                view.Html = GitHubHtmlPage.Wrap("<p>Short replacement</p>");
                await WaitUntilAsync(() => view.Height < 200, "height after replacing long HTML");
                panel.Children.Remove(view);
                await Task.Delay(100);
                Check(view.Content == null, "browser resources not released on unload");
                view.Html = GitHubHtmlPage.Wrap("<h2>Reloaded</h2><p>Ready</p>");
                panel.Children.Add(view);
                await WaitUntilAsync(() => view.Content is WebView2CompositionControl { CoreWebView2: not null }, "reloaded browser");
                var browser = (WebView2CompositionControl)view.Content!;
                for (int i = 0; i < 100; i++)
                {
                    string text = await browser.ExecuteScriptAsync("document.body.innerText");
                    if (text.Contains("Reloaded")) return;
                    await Task.Delay(100);
                }
                throw new Exception("reloaded HTML not rendered");
            }
            finally { window.Close(); }
        });
        await TestAsync("Main window card bindings preserve browsers during version edits and collapse", async () =>
        {
            var window = new MainWindow();
            try
            {
                FakeHandler.Apply(new(_ => Task.FromResult(FakeHandler.Json("{\"resources\":{\"core\":{\"limit\":60,\"remaining\":59,\"reset\":1700000000}}}"))));
                var vm = new RepoViewModel(new() { Owner = "a", Repo = "b" }, () => window.ChkPrerelease.IsChecked == true);
                vm.SetReleases([
                    new() { TagName = "1.1-beta", Prerelease = true, BodyHtml = GitHubHtmlPage.Wrap("<p>Preview</p>") },
                    new() { TagName = "1.0", BodyHtml = GitHubHtmlPage.Wrap("<p>Stable</p>") }
                ]);
                window.Repos.Add(vm);
                window.RepoList.SelectedItem = vm;
                window.Show();
                await WaitUntilAsync(() => Descendants<GitHubMarkdownView>(window).Any(v => v.Content is WebView2CompositionControl { CoreWebView2: not null }), "card initialized");
                var view = Descendants<GitHubMarkdownView>(window).Single();
                var content = view.Content;
                vm.SetCurrent("1.0");
                await Task.Delay(100);
                Check(ReferenceEquals(content, view.Content), "version edit recreated browser");
                window.BtnToggleAllVersions.IsChecked = false;
                await Task.Delay(100);
                Check(!vm.VisibleReleases.Single().IsExpanded && !view.IsVisible, "collapse binding");
                window.BtnToggleAllVersions.IsChecked = true;
                await Task.Delay(100);
                Check(view.IsVisible && ReferenceEquals(content, view.Content), "expand binding/browser lifetime");
                window.ChkPrerelease.IsChecked = true;
                await WaitUntilAsync(() => Descendants<GitHubMarkdownView>(window).Count() == 2, "prerelease cards");
                Check(vm.VisibleReleases.Count() == 2, "prerelease filter");
            }
            finally { window.Close(); }
        });
        await TestAsync("Scrolled browser synchronizes native popup bounds and keeps keyboard focus", async () =>
        {
            var view = new GitHubMarkdownView
            {
                Html = GitHubHtmlPage.Wrap(string.Concat(Enumerable.Range(0, 150).Select(i => $"<p>Selectable line {i}</p>")))
            };
            var scroll = new BrowserScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var window = new Window { Title = "Browser input regression", Width = 800, Height = 500, Content = scroll };
            try
            {
                window.Show();
                await WaitUntilAsync(() => view.Height > 2000, "input page height");
                var browser = (ScrollAwareWebView2)view.Content!;
                var state = typeof(WebView2CompositionControl).GetField("m_webview2Base", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
                var controller = (CoreWebView2Controller)state.GetType().GetProperty("CoreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
                Check(browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled, "native context menu disabled");
                var before = controller.Bounds;
                foreach (double offset in new[] { 500.0, 1000.0, 250.0 })
                {
                    scroll.ScrollToVerticalOffset(offset);
                    await WaitUntilAsync(() => Math.Abs(scroll.VerticalOffset - offset) < 1, "scroll offset");
                    await Task.Delay(50);
                    var origin = browser.TranslatePoint(new Point(), window);
                    double scale = VisualTreeHelper.GetDpi(browser).DpiScaleX;
                    Check(Math.Abs(controller.Bounds.Y - (int)(origin.Y * scale)) <= 1 &&
                        Math.Abs(controller.Bounds.X - (int)(origin.X * scale)) <= 1,
                        $"native popup origin stale at scroll offset {offset}: {controller.Bounds}");
                }
                Check(controller.Bounds.Y < before.Y, "native controller never moved");
                window.Activate();
                browser.Focus();
                await WaitUntilAsync(() => browser.HasFocusWithin(), "native keyboard focus");
                // 原生 Chromium 获得 Win32 焦点后，WPF 的 FocusedElement 可以为空。
                Check(Keyboard.FocusedElement is not ScrollViewer && !scroll.Focusable, "scroll viewer took focus from browser");
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        for (int i = 0; i < 150; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }
}
