using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        /// 当鼠标在主窗口内且位于某个 WebBrowser 的屏幕矩形上方时，
        /// 把滚轮偏移量转给外层 RightScrollViewer。
        /// 若 JS 桥接已经在处理同一滚轮事件，则跳过避免双倍滚动。
        /// </summary>
        private void TryScrollOuter(int x, int y, short delta)
        {
            if (_window.RightScrollViewer == null) return;

            // 只有主窗口是当前前景窗口时才处理。打开设置窗口等模态对话框时，
            // 前景窗口是子窗口，此时应忽略滚轮，避免"穿透"到主窗口右侧信息面板。
            if (GetForegroundWindow() != _mainWindowHandle) return;

            // JS 桥接已成功处理时，低层钩子不再重复滚动（时间窗口 100ms）。
            if (DateTime.Now - _window.LastScriptWheel < TimeSpan.FromMilliseconds(100))
                return;

            var point = new Point(x, y);
            var windowRect = GetScreenRect(_window);
            if (!windowRect.Contains(point)) return;

            foreach (var wb in FindVisualChildren<System.Windows.Controls.WebBrowser>(_window))
            {
                if (!wb.IsLoaded || !wb.IsVisible) continue;
                var wbRect = GetScreenRect(wb);
                if (wbRect.Contains(point))
                {
                    double lines = SystemParameters.WheelScrollLines;
                    if (lines <= 0) lines = 3;
                    double offset = delta / (double)Mouse.MouseWheelDeltaForOneLine * lines * 16.0;
                    _window.RightScrollViewer.ScrollToVerticalOffset(
                        _window.RightScrollViewer.VerticalOffset - offset);
                    return;
                }
            }
        }

        private static Rect GetScreenRect(FrameworkElement element)
        {
            try
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                return new Rect(topLeft, bottomRight);
            }
            catch
            {
                return Rect.Empty;
            }
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
