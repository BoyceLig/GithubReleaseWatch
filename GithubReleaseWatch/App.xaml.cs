using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GithubReleaseWatch
{
    public partial class App : System.Windows.Application
    {
        /// <summary>单实例互斥体，用于限制程序只能同时运行一个实例。</summary>
        private Mutex? _singleInstanceMutex;

        /// <summary>标识当前实例是否成功创建了互斥体（即是否为第一个实例）。</summary>
        private bool _ownsMutex;

        /// <summary>命名事件：其他实例通过 Set 此事件通知本实例显示主窗口。</summary>
        private const string ShowEventName = "GithubReleaseWatch_ShowEvent";

        /// <summary>命名事件：其他实例通过 Set 此事件让本实例弹出"已在运行"模态提示。</summary>
        private const string AlertEventName = "GithubReleaseWatch_AlertEvent";

        private EventWaitHandle? _showEvent;
        private EventWaitHandle? _alertEvent;
        private CancellationTokenSource? _eventCts;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "GithubReleaseWatch_SingleInstance_Mutex";

            // 尝试创建命名互斥体。若已有同名互斥体，说明已有实例在运行。
            _singleInstanceMutex = new Mutex(true, mutexName, out _ownsMutex);

            if (!_ownsMutex)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            // 第一个实例：创建命名事件并启动后台线程，等待其他实例唤醒主窗口或弹出提示。
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _alertEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AlertEventName);
            _eventCts = new CancellationTokenSource();
            _ = Task.Run(() => WaitForEvents(_eventCts.Token));

            base.OnStartup(e);

            // 主窗口改为手动创建：StartupUri 会让第二个实例也创建 MainWindow，
            // 随后 Shutdown() 关闭它时会触发第二实例的 OnClosing，导致误弹"已最小化"通知。
            var mainWindow = new GithubReleaseWatch.MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _eventCts?.Cancel();
            try { _showEvent?.Set(); } catch { /* 唤醒等待线程以便退出 */ }
            try { _alertEvent?.Set(); } catch { }
            _showEvent?.Dispose();
            _alertEvent?.Dispose();

            if (_ownsMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        /// <summary>后台线程：等待其他实例要求显示主窗口或弹出提示。</summary>
        private void WaitForEvents(CancellationToken token)
        {
            var handles = new WaitHandle[] { _showEvent!, _alertEvent! };
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int index = WaitHandle.WaitAny(handles);
                    if (token.IsCancellationRequested) break;

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is not MainWindow main) return;
                        if (index == 0) main.ShowMainWindow();
                        else if (index == 1) main.ShowAlreadyRunningAlert();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { /* 忽略异常，继续等待 */ }
            }
        }

        #region Win32 API

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

        #endregion

        /// <summary>
        /// 激活已存在的实例窗口：通过命名事件通知它显示自己并弹出模态提示，
        /// 这样提示框可以以主窗口为 owner，实现不关闭弹窗无法操作主窗口。
        /// </summary>
        private void ActivateExistingInstance()
        {
            // 通知已有实例：先恢复窗口，再弹出模态提示。
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch { /* 事件不存在则忽略 */ }

            try
            {
                using var alertEvent = EventWaitHandle.OpenExisting(AlertEventName);
                alertEvent.Set();
            }
            catch { /* 事件不存在则忽略 */ }
        }
    }
}
