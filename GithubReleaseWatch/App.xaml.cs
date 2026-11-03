using System;
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

        /// <summary>命名事件：其他实例通过 Set 此事件让本实例弹出"已在运行"模态提示。</summary>
        private const string AlertEventName = "GithubReleaseWatch_AlertEvent";

        private EventWaitHandle? _alertEvent;
        private CancellationTokenSource? _eventCts;
        private Task? _eventTask;

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

            // 第一个实例：创建命名事件并启动后台线程，等待其他实例弹出"已在运行"提示。
            _alertEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AlertEventName);
            _eventCts = new CancellationTokenSource();
            var token = _eventCts.Token;
            _eventTask = Task.Run(() => WaitForEvents(token));

            base.OnStartup(e);

            // 主窗口改为手动创建：StartupUri 会让第二个实例也创建 MainWindow，
            // 导致本应直接退出的第二实例也弹出主窗口。
            var mainWindow = new GithubReleaseWatch.MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _eventCts?.Cancel();
            _eventTask?.GetAwaiter().GetResult();
            _alertEvent?.Dispose();
            _eventCts?.Dispose();

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
            while (!token.IsCancellationRequested)
            {
                try
                {
                    WaitHandle.WaitAny(new WaitHandle[] { _alertEvent!, token.WaitHandle });
                    if (token.IsCancellationRequested) break;
                    if (Dispatcher.HasShutdownStarted) break;

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow main) main.ShowAlreadyRunningAlert();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
            }
        }

        /// <summary>
        /// 激活已存在的实例窗口：通过命名事件通知它显示自己并弹出模态提示，
        /// 这样提示框可以以主窗口为 owner，实现不关闭弹窗无法操作主窗口。
        /// </summary>
        private void ActivateExistingInstance()
        {
            // 通知已有实例弹出"已在运行"模态提示。
            try
            {
                using var alertEvent = EventWaitHandle.OpenExisting(AlertEventName);
                alertEvent.Set();
            }
            catch { /* 事件不存在则忽略 */ }
        }
    }
}
