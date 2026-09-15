using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Point = System.Windows.Point;

namespace GithubReleaseWatch
{
    /// <summary>
    /// 全局低层鼠标钩子兜底：WPF WebBrowser (HwndHost) 会吞掉内部 wheel 事件，
    /// 导致外层 ScrollViewer 无法滚动。本钩子监听所有 WM_MOUSEWHEEL，
    /// 当鼠标位于主窗口内某个 WebBrowser 上方时，直接把滚动偏移转给外层 ScrollViewer。
    /// </summary>
    public sealed class MouseWheelHook : IDisposable
    {
        private readonly MainWindow _window;
        private readonly IntPtr _mainWindowHandle;
        private readonly LowLevelMouseProc _callback;
        private IntPtr _hook;

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX;
            public int ptY;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        public MouseWheelHook(MainWindow window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _mainWindowHandle = new WindowInteropHelper(window).EnsureHandle();
            _callback = HookCallback;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL && _window.IsLoaded)
            {
                try
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    short delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    _window.Dispatcher.BeginInvoke(() => TryScrollOuter(info.ptX, info.ptY, delta));
                }
                catch { }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 当鼠标在主窗口右侧滚动区的可视范围内时，把滚轮增量转给外层 RightScrollViewer。
        /// 用 ScrollViewer 的视口矩形判断命中，而不是各个 WebBrowser 的元素矩形——
        /// WebBrowser 被滚出视口时元素矩形仍在固定区上方，用元素矩形判断会"鼠标在标题上却滚了内容"。
        /// </summary>
        private void TryScrollOuter(int x, int y, short delta)
        {
            var scrollViewer = _window.RightScrollViewer;
            if (scrollViewer == null || !scrollViewer.IsVisible) return;

            // 只有主窗口是当前前景窗口时才处理。打开设置窗口等模态对话框时，
            // 前景窗口是子窗口，此时应忽略滚轮，避免"穿透"到主窗口右侧信息面板。
            if (GetForegroundWindow() != _mainWindowHandle) return;

            var viewport = WebBrowserAirspaceHelper.GetViewportScreenRect(scrollViewer);
            if (viewport.IsEmpty || !viewport.Contains(new Point(x, y))) return;

            // JS 桥接已成功处理时，低层钩子不再重复滚动（时间窗口 100ms）。
            if (DateTime.Now - _window.LastScriptWheel < TimeSpan.FromMilliseconds(100))
                return;

            _window.WheelScrollByRawDelta(delta);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
