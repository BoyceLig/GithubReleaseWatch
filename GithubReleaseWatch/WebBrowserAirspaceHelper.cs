using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 修复 WPF WebBrowser (HwndHost) 的 Airspace 层级问题：
    /// WebBrowser 的 HWND 始终绘制在 WPF 内容上方，滚动/布局变化时可能覆盖上下控件。
    /// 本类通过 SetWindowRgn 把 WebBrowser 宿主窗口裁剪到外层 ScrollViewer 的可见视口。
    ///
    /// 性能要点：
    /// 1. SetWindowRgn 用 bRedraw=true —— 系统原子协调 WPF+IE 重绘，零拖影。
    ///    成本由调用方「1帧最多1次裁剪」的降频控制（v2.31 实测 1.07次/帧）。
    /// 2. 缓存上次裁剪区，区域没变直接 return（同一帧内被多处调用也不会重复设区域）。
    /// 3. 完全滚出视口时返回零尺寸矩形（整窗裁掉），避免压住冻结区。
    /// </summary>
    public static class WebBrowserAirspaceHelper
    {
        /// <summary>缓存每个 WebBrowser 上次应用的裁剪区域，避免重复调用 SetWindowRgn。</summary>
        private static readonly ConditionalWeakTable<System.Windows.Controls.WebBrowser, ClipState> s_lastClips = new();

        private sealed class ClipState
        {
            /// <summary>上次应用的裁剪区（HWND 客户区物理像素）。</summary>
            public Rect Clip { get; set; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// 一次裁剪所需的公共参数：ScrollViewer 的可见视口在屏幕上的矩形。
        /// 批量裁剪多个 WebBrowser 时只算一次，省掉每个浏览器各查一次视口。
        /// </summary>
        public readonly struct ClipContext
        {
            public ClipContext(Rect viewportScreenRect) => ViewportScreenRect = viewportScreenRect;

            public Rect ViewportScreenRect { get; }
        }

        /// <summary>构造裁剪上下文（视口矩形取 ScrollViewer 的可见视口）。</summary>
        public static ClipContext CreateClipContext(ScrollViewer scrollViewer) =>
            new ClipContext(GetViewportScreenRect(scrollViewer));

        /// <summary>把 WebBrowser 的 HWND 裁剪到指定 ScrollViewer 的可见视口。</summary>
        public static void ClipToScrollViewerViewport(System.Windows.Controls.WebBrowser webBrowser, ScrollViewer scrollViewer)
        {
            if (scrollViewer == null || !scrollViewer.IsVisible) return;
            ClipToViewportRect(webBrowser, CreateClipContext(scrollViewer));
        }

        /// <summary>
        /// 把 WebBrowser 的 HWND 裁剪到给定的视口矩形（屏幕坐标 DIP）。
        /// 批量裁剪时先建一次 ClipContext 再逐个调用，省掉重复的视口/DPI 计算。
        /// </summary>
        public static void ClipToViewportRect(System.Windows.Controls.WebBrowser webBrowser, ClipContext context)
        {
            if (webBrowser == null || !webBrowser.IsLoaded || !webBrowser.IsVisible) return;
            if (context.ViewportScreenRect.IsEmpty) return;

            var hWnd = webBrowser.Handle;
            if (hWnd == IntPtr.Zero) return;

            try
            {
                if (!GetClientRect(hWnd, out var client)) return;
                int clientWidth = client.Right - client.Left;
                int clientHeight = client.Bottom - client.Top;
                if (clientWidth <= 0 || clientHeight <= 0) return;

                var browserScreenRect = GetScreenRect(webBrowser);
                if (browserScreenRect.IsEmpty) return;
                if (browserScreenRect.Width <= 0 || browserScreenRect.Height <= 0) return;

                // 单位换算系数：PointToScreen 拿到的是屏幕坐标，换算成 HWND 客户区像素时不能直接乘 DPI 缩放
                // （PointToScreen 的返回值是否已含 DPI 缩放，各 .NET 版本/DPI 感知模式下表述不一，
                //   直接乘 TransformToDevice 在 150% 缩放下会二次放大）。
                // 这里改用实测比例：客户区像素尺寸 / 屏幕矩形尺寸，无论哪种单位都换算正确，
                // 100% 缩放下系数恒为 1，与旧行为完全一致。
                double toClientX = clientWidth / browserScreenRect.Width;
                double toClientY = clientHeight / browserScreenRect.Height;

                var newClip = ComputeClipRect(browserScreenRect, context.ViewportScreenRect,
                    toClientX, toClientY, clientWidth, clientHeight);

                if (s_lastClips.TryGetValue(webBrowser, out var state))
                {
                    if (AreClose(newClip, state.Clip)) return;
                    ApplyClip(hWnd, newClip);
                    state.Clip = newClip;
                }
                else
                {
                    ApplyClip(hWnd, newClip);
                    s_lastClips.Add(webBrowser, new ClipState { Clip = newClip });
                }
            }
            catch { /* 裁剪失败不影响功能 */ }
        }

        /// <summary>重置 WebBrowser 的窗口区域为完整矩形（取消裁剪）。</summary>
        public static void ResetClip(System.Windows.Controls.WebBrowser webBrowser)
        {
            if (webBrowser == null) return;
            try
            {
                var hWnd = webBrowser.Handle;
                if (hWnd == IntPtr.Zero) return;
                // 恢复整窗可见，必须让系统重绘
                SetWindowRgn(hWnd, IntPtr.Zero, true);
                s_lastClips.Remove(webBrowser);
            }
            catch { }
        }

        /// <summary>
        /// 计算 HWND 客户区内应保留的裁剪矩形（物理像素）。
        /// 完全滚出视口时返回零尺寸矩形 —— 相当于整窗裁掉，避免它压在固定区上。
        /// </summary>
        private static Rect ComputeClipRect(Rect browserScreenRect, Rect viewportScreenRect,
            double toClientX, double toClientY, int clientWidth, int clientHeight)
        {
            var visible = Rect.Intersect(browserScreenRect, viewportScreenRect);
            if (visible.IsEmpty || visible.Width <= 0.5 || visible.Height <= 0.5)
                return new Rect(0, 0, 0, 0);

            int left = (int)Math.Round((visible.X - browserScreenRect.X) * toClientX);
            int top = (int)Math.Round((visible.Y - browserScreenRect.Y) * toClientY);
            int right = left + (int)Math.Round(visible.Width * toClientX);
            int bottom = top + (int)Math.Round(visible.Height * toClientY);

            if (left < 0) left = 0;
            if (top < 0) top = 0;
            if (right > clientWidth) right = clientWidth;
            if (bottom > clientHeight) bottom = clientHeight;
            if (right < left) right = left;
            if (bottom < top) bottom = top;

            return new Rect(left, top, right - left, bottom - top);
        }

        private static void ApplyClip(IntPtr hWnd, Rect clip)
        {
            // SetWindowRgn 成功后 hrgn 的所有权归系统，不需要（也不能）DeleteObject
            var hrgn = CreateRectRgn((int)clip.Left, (int)clip.Top, (int)clip.Right, (int)clip.Bottom);
            if (hrgn == IntPtr.Zero) return;
            // bRedraw=true：系统原子协调 WPF+IE 重绘，零拖影。
            // 调用方保证一帧最多一次，成本可控。
            SetWindowRgn(hWnd, hrgn, true);
        }

        private static bool AreClose(Rect a, Rect b)
        {
            const double tolerance = 0.5;
            return Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance
                && Math.Abs(a.Width - b.Width) < tolerance && Math.Abs(a.Height - b.Height) < tolerance;
        }

        /// <summary>获取元素在屏幕上的矩形。返回单位与 GetViewportScreenRect 一致，两者可直接求交。</summary>
        private static Rect GetScreenRect(FrameworkElement element)
        {
            try
            {
                var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
                var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
                return new Rect(topLeft, bottomRight);
            }
            catch
            {
                return Rect.Empty;
            }
        }

        /// <summary>获取 ScrollViewer 可见视口在屏幕上的矩形（左上角起算，尺寸取 ViewportWidth/Height）。</summary>
        public static Rect GetViewportScreenRect(ScrollViewer scrollViewer)
        {
            try
            {
                var topLeft = scrollViewer.PointToScreen(new System.Windows.Point(0, 0));
                var bottomRight = scrollViewer.PointToScreen(
                    new System.Windows.Point(scrollViewer.ViewportWidth, scrollViewer.ViewportHeight));
                return new Rect(topLeft, bottomRight);
            }
            catch
            {
                return Rect.Empty;
            }
        }
    }
}
