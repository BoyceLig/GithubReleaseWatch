using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;

namespace GithubReleaseWatch;

/// <summary>浏览器已获得焦点时，让 Chromium 处理按键，避免 ScrollViewer 抢回焦点。</summary>
public sealed class BrowserScrollViewer : ScrollViewer
{
    public BrowserScrollViewer() => Focusable = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.OriginalSource is WebView2CompositionControl || Keyboard.FocusedElement is WebView2CompositionControl)
            return;
        base.OnKeyDown(e);
    }
}
