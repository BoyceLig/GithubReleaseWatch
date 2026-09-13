using System;
using System.Runtime.InteropServices;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 供 WebBrowser 内 JavaScript 调用的桥接对象。
    /// 用于把 HTML 内部的 wheel 事件转发给 WPF，从而让 WebBrowser 参与外层 ScrollViewer 滚动。
    /// </summary>
    [ComVisible(true)]
    public class WebBrowserScriptBridge
    {
        /// <summary>请求外层 ScrollViewer 滚动的偏移量（向上为正，向下为负）。</summary>
        public event Action<int>? ScrollOuterRequested;

        public void ScrollOuter(int delta)
        {
            ScrollOuterRequested?.Invoke(delta);
        }
    }
}