using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;

namespace GithubReleaseWatch;

/// <summary>同步外层滚动造成的位置变化，保留 Chromium 的原生输入和右键菜单。</summary>
internal sealed class ScrollAwareWebView2 : WebView2CompositionControl
{
    // SDK 1.0.4314.35 只在初始化/SizeChanged 时同步 Bounds，没有公开同步接口。
    // 将兼容处理限制在此处，复用 SDK 自身的 DPI/坐标转换；升级 SDK 时由浏览器回归测试检查。
    private static readonly Action<WebView2CompositionControl> SyncBounds =
        (typeof(WebView2CompositionControl).GetMethod("SyncControllerBounds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new NotSupportedException("当前 WebView2 SDK 缺少滚动位置同步接口。"))
        .CreateDelegate<Action<WebView2CompositionControl>>();

    private Rect? _lastScreenBounds;

    public ScrollAwareWebView2()
    {
        Focusable = true;
        Loaded += (_, _) => LayoutUpdated += OnLayoutUpdated;
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            _lastScreenBounds = null;
        };
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => SynchronizePosition();

    internal void SynchronizePosition()
    {
        if (!IsLoaded || !IsVisible || CoreWebView2 == null || PresentationSource.FromVisual(this) == null) return;
        var bounds = new Rect(PointToScreen(new Point()), PointToScreen(new Point(ActualWidth, ActualHeight)));
        if (_lastScreenBounds == bounds) return;
        SyncBounds(this);
        _lastScreenBounds = bounds;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        SynchronizePosition();
        if (!HasFocusWithin()) Focus();
        // SDK 按下时使用上次 MouseMove 的位置。滚轮后鼠标可能没动，必须重新取当前位置。
        base.OnMouseMove(e);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        SynchronizePosition();
        base.OnMouseMove(e);
        base.OnMouseUp(e);
    }
}
