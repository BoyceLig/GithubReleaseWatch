using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 修复 WPF WebBrowser (HwndHost) 的 Airspace 层级问题：
    /// WebBrowser 的 HWND 始终绘制在 WPF 内容上方，滚动/布局变化时可能覆盖上下控件。
    /// 本类通过 SetWindowRgn 把 WebBrowser 宿主窗口裁剪到外层 ScrollViewer 的可见视口。
    /// </summary>
    public static class WebBrowserAirspaceHelper
    {
        /// <summary>缓存每个 WebBrowser 上次应用的裁剪区域，避免重复调用 SetWindowRgn。</summary>
        private static readonly ConditionalWeakTable<System.Windows.Controls.WebBrowser, ClipRectHolder> s_lastClipRects = new();

        private sealed class ClipRectHolder
        {
            public Rect Rect { get; set; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

        [DllImport("gdi32.dll")]
        private static extern int DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int RGN_AND = 1;
        private const int LOGPIXELSX = 88;
        private const int LOGPIXELSY = 90;

        /// <summary>把 WebBrowser 的 HWND 裁剪到指定 ScrollViewer 的可见视口。</summary>
        public static void ClipToScrollViewerViewport(System.Windows.Controls.WebBrowser webBrowser, ScrollViewer scrollViewer)
        {
            if (webBrowser == null || !webBrowser.IsLoaded || !webBrowser.IsVisible) return;
            if (scrollViewer == null || !scrollViewer.IsVisible) return;

            var hWnd = webBrowser.Handle;
            if (hWnd == IntPtr.Zero) return;

            try
            {
                // WebBrowser 在屏幕上的矩形（DIP）
                var browserScreenRect = GetScreenRect(webBrowser);
                if (browserScreenRect.IsEmpty) return;

                // ScrollViewer 视口在屏幕上的矩形（DIP）
                var viewportScreenRect = GetViewportScreenRect(scrollViewer);
                if (viewportScreenRect.IsEmpty) return;

                // 交集：WebBrowser 在 ScrollViewer 视口内的可见部分
                var visible = Rect.Intersect(browserScreenRect, viewportScreenRect);
                if (visible.IsEmpty) return;

                // 转换为 HWND 客户区物理像素坐标
                var scale = GetDpiScale(webBrowser);
                var browserClient = new Rect(
                    (visible.X - browserScreenRect.X) * scale.X,
                    (visible.Y - browserScreenRect.Y) * scale.Y,
                    visible.Width * scale.X,
                    visible.Height * scale.Y);

                int left = (int)Math.Round(browserClient.X);
                int top = (int)Math.Round(browserClient.Y);
                int right = left + (int)Math.Round(browserClient.Width);
                int bottom = top + (int)Math.Round(browserClient.Height);

                if (left < 0) left = 0;
                if (top < 0) top = 0;
                if (right <= left || bottom <= top) return;

                // 与上次裁剪区域相同则跳过，减少 SetWindowRgn 调用（避免拖动滚动条时闪烁/撕裂）。
                var newClip = new Rect(left, top, right - left, bottom - top);
                if (s_lastClipRects.TryGetValue(webBrowser, out var holder))
                {
                    if (AreClose(newClip, holder.Rect)) return;
                    holder.Rect = newClip;
                }
                else
                {
                    s_lastClipRects.Add(webBrowser, new ClipRectHolder { Rect = newClip });
                }

                // 创建裁剪区域并应用。注意：SetWindowRgn 会获得 hrgn 的所有权，
                // 成功后 GDI 会自己释放，这里不需要 DeleteObject。
                var hrgn = CreateRectRgn(left, top, right, bottom);
                // bRedraw=false：WPF 会负责后续重绘，避免强制重绘带来的撕裂感。
                SetWindowRgn(hWnd, hrgn, false);
            }
            catch { /* 裁剪失败不影响功能 */ }
        }

        private static bool AreClose(Rect a, Rect b)
        {
            const double tolerance = 0.5;
            return Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance
                && Math.Abs(a.Width - b.Width) < tolerance && Math.Abs(a.Height - b.Height) < tolerance;
        }

        /// <summary>重置 WebBrowser 的窗口区域为完整矩形（取消裁剪）。</summary>
        public static void ResetClip(System.Windows.Controls.WebBrowser webBrowser)
        {
            if (webBrowser == null) return;
            try
            {
                var hWnd = webBrowser.Handle;
                if (hWnd == IntPtr.Zero) return;
                SetWindowRgn(hWnd, IntPtr.Zero, false);
                s_lastClipRects.Remove(webBrowser);
            }
            catch { }
        }

        /// <summary>获取元素在屏幕上的矩形（DIP）。</summary>
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

        /// <summary>获取 ScrollViewer 可见视口在屏幕上的矩形（DIP）。</summary>
        private static Rect GetViewportScreenRect(ScrollViewer scrollViewer)
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

        /// <summary>获取元素所在窗口的 DPI 缩放比例。</summary>
        private static (double X, double Y) GetDpiScale(Visual visual)
        {
            try
            {
                var source = PresentationSource.FromVisual(visual);
                if (source?.CompositionTarget != null)
                {
                    var transform = source.CompositionTarget.TransformToDevice;
                    return (transform.M11, transform.M22);
                }
            }
            catch { }

            // 兜底：用设备上下文计算
            try
            {
                var desktop = GetDC(IntPtr.Zero);
                if (desktop != IntPtr.Zero)
                {
                    int dx = GetDeviceCaps(desktop, LOGPIXELSX);
                    int dy = GetDeviceCaps(desktop, LOGPIXELSY);
                    ReleaseDC(IntPtr.Zero, desktop);
                    return (dx / 96.0, dy / 96.0);
                }
            }
            catch { }

            return (1.0, 1.0);
        }
    }
}
