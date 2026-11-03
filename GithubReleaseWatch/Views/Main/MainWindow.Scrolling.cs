using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace GithubReleaseWatch;

public partial class MainWindow
{
    // ===== 滚轮穿透：版本卡片内的浏览器/Border 把滚轮事件转给外层 ScrollViewer =====

    /// <summary>
    /// 鼠标在版本信息卡片内滚动时，直接转发给外层 RightScrollViewer，
    /// 让右侧标题栏固定、三个版本信息共用外层面板滚动。
    /// </summary>
    private void VersionBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelScrollByRawDelta(e.Delta);
    }

    /// <summary>拖动滚动条时停止平滑动画，避免争抢偏移量。</summary>
    private void RightScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (RightScrollViewer == null) return;

        if (_smoothScrollRunning)
        {
            // 动画期间偏移量应该只由本类写。出现别的值说明用户正在拖滚动条，
            // 此时立刻让位给用户操作（否则动画会跟拖动抢偏移量，手感发黏）。
            if (Math.Abs(e.VerticalOffset - _lastAppliedOffset) > 0.5)
                StopSmoothScroll();
        }

        if (!_smoothScrollRunning)
            _smoothScrollTarget = e.VerticalOffset;


    }

    /// <summary>订阅平滑滚动的渲染帧回调。</summary>
    private void EnsureRenderFrameHook()
    {
        if (_frameHookAttached) return;
        _frameHookAttached = true;
        _frameLastTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void DetachRenderFrameHook()
    {
        if (!_frameHookAttached) return;
        _frameHookAttached = false;
        CompositionTarget.Rendering -= OnRenderFrame;
    }

    /// <summary>
    /// 每渲染帧推进 WPF 平滑滚动。
    /// </summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _frameLastTicks) / (double)Stopwatch.Frequency;
        _frameLastTicks = now;

        if (_smoothScrollRunning) AdvanceSmoothScroll(elapsed);
        if (!_smoothScrollRunning) DetachRenderFrameHook();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var grandChild in FindVisualChildren<T>(child))
                yield return grandChild;
        }
    }

    /// <summary>卡片上的 WPF 滚轮与浏览器消息共用滚动入口。</summary>
    internal void WheelScrollByRawDelta(int wheelDelta)
    {
        if (wheelDelta == 0) return;
        WheelScrollBy(WheelDeltaToOffset(wheelDelta));
    }

    /// <summary>
    /// 按增量滚动右侧面板（delta &gt; 0 表示向上滚），带平滑动画。
    /// 直接把偏移量一步跳到目标值就是"一格一格翻页"的来源，这里让它在几个渲染帧内指数逼近目标。
    /// </summary>
    private void WheelScrollBy(double delta)
    {
        var scrollViewer = RightScrollViewer;
        if (scrollViewer == null || delta == 0) return;

        if (!_smoothScrollRunning) _smoothScrollTarget = scrollViewer.VerticalOffset;

        // 目标以"上次目标"为基准累加，连续快滚时不会因为动画没跟上而吞掉滚动量
        _smoothScrollTarget = Math.Clamp(_smoothScrollTarget - delta, 0, scrollViewer.ScrollableHeight);

        // 系统关掉了动画效果（辅助功能/性能选项）时保持原来的瞬时滚动
        if (!SystemParameters.ClientAreaAnimation)
        {
            scrollViewer.ScrollToVerticalOffset(_smoothScrollTarget);
            return;
        }

        _smoothScrollRunning = true;
        EnsureRenderFrameHook();
    }

    /// <summary>按真实帧间隔向目标偏移量做指数逼近，手感接近现代浏览器的平滑滚动。</summary>
    private void AdvanceSmoothScroll(double elapsed)
    {
        var scrollViewer = RightScrollViewer;
        if (scrollViewer == null)
        {
            StopSmoothScroll();
            return;
        }

        if (elapsed <= 0) return;
        if (elapsed > 0.1) elapsed = 0.1; // 掉帧/窗口挂起后不要一步跳过去

        double current = scrollViewer.VerticalOffset;
        _smoothScrollTarget = Math.Clamp(_smoothScrollTarget, 0, scrollViewer.ScrollableHeight);
        double remaining = _smoothScrollTarget - current;

        if (Math.Abs(remaining) < 0.5)
        {
            _lastAppliedOffset = _smoothScrollTarget;
            scrollViewer.ScrollToVerticalOffset(_smoothScrollTarget);
            StopSmoothScroll();
            return;
        }

        double ratio = 1.0 - Math.Exp(-elapsed / SmoothScrollTau);
        // 原生浏览器只能定位到物理像素；避免小数 DIP 让文字反复抖动。
        double scale = VisualTreeHelper.GetDpi(scrollViewer).DpiScaleY;
        _lastAppliedOffset = Math.Round((current + remaining * ratio) * scale) / scale;
        if (Math.Abs(_lastAppliedOffset - current) < 0.01)
            _lastAppliedOffset = _smoothScrollTarget;
        scrollViewer.ScrollToVerticalOffset(_lastAppliedOffset);

    }

    private void StopSmoothScroll()
    {
        _smoothScrollRunning = false;
        DetachRenderFrameHook();
    }


    /// <summary>
    /// 把鼠标滚轮 delta 转换为与 WPF ScrollViewer 默认滚动速度一致的偏移量（DIP）。
    /// WPF 默认每 120 delta 滚动 SystemParameters.WheelScrollLines 行，每行约 16 DIP。
    /// </summary>
    private double WheelDeltaToOffset(double delta)
    {
        double lines = SystemParameters.WheelScrollLines;
        if (lines < 0) return delta / Mouse.MouseWheelDeltaForOneLine * RightScrollViewer.ViewportHeight;
        if (lines == 0) return 0;
        return delta / Mouse.MouseWheelDeltaForOneLine * lines * 16.0;
    }

    // 渲染帧只负责 WPF 平滑滚动，裁剪由 WPF 可视树处理。
    /// <summary>是否已订阅 CompositionTarget.Rendering。</summary>
    private bool _frameHookAttached;
    /// <summary>上一渲染帧的时间戳（Stopwatch）。</summary>
    private long _frameLastTicks;

    // ===== 平滑滚动 =====
    /// <summary>平滑滚动的目标偏移量（DIP）。滚轮每来一格累积到这里，由渲染帧逐步逼近。</summary>
    private double _smoothScrollTarget;
    /// <summary>是否正在平滑滚动。动画期间的 ScrollChanged 是自己触发的，不再回写目标值。</summary>
    private bool _smoothScrollRunning;
    /// <summary>本类最后一次写入 ScrollViewer 的偏移量，用来识别"偏移被别人改了"。</summary>
    private double _lastAppliedOffset;
    /// <summary>指数逼近的时间常数（秒）。越小越跟手。</summary>
    private const double SmoothScrollTau = 0.055;

}
