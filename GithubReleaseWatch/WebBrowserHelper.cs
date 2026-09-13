using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 让 WPF WebBrowser 支持绑定 HTML 字符串的附加属性。
    /// 加载完成后自动根据文档高度调整 WebBrowser 高度，使外层 ScrollViewer 统一滚动。
    /// </summary>
    public static class WebBrowserHelper
    {
        public static readonly DependencyProperty BindableHtmlProperty =
            DependencyProperty.RegisterAttached(
                "BindableHtml",
                typeof(string),
                typeof(WebBrowserHelper),
                new PropertyMetadata(null, OnBindableHtmlChanged));

        public static readonly DependencyProperty AutoHeightProperty =
            DependencyProperty.RegisterAttached(
                "AutoHeight",
                typeof(bool),
                typeof(WebBrowserHelper),
                new PropertyMetadata(false));

        public static string? GetBindableHtml(DependencyObject obj) =>
            (string?)obj.GetValue(BindableHtmlProperty);

        public static void SetBindableHtml(DependencyObject obj, string? value) =>
            obj.SetValue(BindableHtmlProperty, value);

        public static bool GetAutoHeight(DependencyObject obj) =>
            (bool)obj.GetValue(AutoHeightProperty);

        public static void SetAutoHeight(DependencyObject obj, bool value) =>
            obj.SetValue(AutoHeightProperty, value);

        private static void OnBindableHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.WebBrowser webBrowser)
            {
                webBrowser.LoadCompleted -= OnLoadCompleted;
                webBrowser.LoadCompleted += OnLoadCompleted;

                // 监听大小变化，及时修复 WebBrowser Airspace 层级问题。
                // ScrollViewer 滚动时的裁剪由 MainWindow.RightScrollViewer_ScrollChanged 统一处理，
                // 不再监听 LayoutUpdated，避免高频重绘带来的撕裂感。
                webBrowser.SizeChanged -= OnBrowserSizeChanged;
                webBrowser.SizeChanged += OnBrowserSizeChanged;

                var html = e.NewValue as string;
                if (string.IsNullOrEmpty(html))
                    webBrowser.NavigateToString("<html><body></body></html>");
                else
                    webBrowser.NavigateToString(html);
            }
        }

        private static void OnBrowserSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.WebBrowser webBrowser)
                ClipBrowserToViewport(webBrowser);
        }

        /// <summary>把 WebBrowser 裁剪到外层 ScrollViewer 的可见视口，防止覆盖上下控件。</summary>
        private static void ClipBrowserToViewport(System.Windows.Controls.WebBrowser webBrowser)
        {
            var scrollViewer = FindVisualParent<ScrollViewer>(webBrowser);
            if (scrollViewer != null)
            {
                WebBrowserAirspaceHelper.ClipToScrollViewerViewport(webBrowser, scrollViewer);
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void OnLoadCompleted(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            if (sender is not System.Windows.Controls.WebBrowser webBrowser) return;

            // 建立脚本桥接：文档加载完成且 WebBrowser 已在窗口中时才能拿到所属 MainWindow
            EnsureScriptBridge(webBrowser);

            if (!GetAutoHeight(webBrowser)) return;

            try
            {
                // 等待 IE 渲染完成后再取高度
                webBrowser.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        dynamic? doc = webBrowser.Document;
                        if (doc is null) return;
                        dynamic? body = doc.body;
                        if (body is null) return;
                        int scrollHeight = (int)body.scrollHeight;
                        int padding = 4; // 防止底部截断
                        webBrowser.Height = Math.Max(scrollHeight + padding, 30);

                        // 高度调整后重新裁剪，确保不会超出视口
                        ClipBrowserToViewport(webBrowser);
                    }
                    catch { /* COM/动态失败时保持原高度 */ }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private static void EnsureScriptBridge(System.Windows.Controls.WebBrowser webBrowser)
        {
            try
            {
                if (webBrowser.ObjectForScripting is not WebBrowserScriptBridge bridge)
                {
                    bridge = new WebBrowserScriptBridge();
                    webBrowser.ObjectForScripting = bridge;
                }

                if (System.Windows.Window.GetWindow(webBrowser) is MainWindow mainWindow)
                {
                    bridge.ScrollOuterRequested -= mainWindow.ScrollOuter;
                    bridge.ScrollOuterRequested += mainWindow.ScrollOuter;
                }
            }
            catch { /* 桥接失败不影响渲染 */ }
        }
    }
}
