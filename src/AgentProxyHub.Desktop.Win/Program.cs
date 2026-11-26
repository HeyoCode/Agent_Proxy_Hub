namespace AgentProxyHub.Desktop.Win;

internal static class Program
{
    private const string SingleInstanceMutexName = "AgentProxyHub_SingleInstance_Lock";
    private const string ActivateEventName = "AgentProxyHub_Activate_Signal";
    private const string ShutdownEventName = "AgentProxyHub_Shutdown_Signal";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;
    private static EventWaitHandle? _shutdownSignal;
    private static RegisteredWaitHandle? _activateWaitHandle;
    private static RegisteredWaitHandle? _shutdownWaitHandle;

    [STAThread]
    private static void Main(string[] args)
    {
        bool isShutdownCmd = args.Any(a => a.Equals("--shutdown", StringComparison.OrdinalIgnoreCase));
        if (isShutdownCmd)
        {
            TrySignalExistingShutdownAndWait();
            return;
        }

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // 若托盘已有实例在运行，唤醒其无边框主窗口而不是弹窗打断用户
            try
            {
                using var existingSignal = EventWaitHandle.OpenExisting(ActivateEventName);
                existingSignal.Set();
            }
            catch
            {
                MessageBox.Show("Agent Proxy Hub 已在系统托盘中运行，请双击托盘图标打开控制台。",
                    "Agent Proxy Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        string[] hostArgs = args.Where(a => !a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)).ToArray();

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);

        // 在桌面端同进程后台拉起跨平台核心代理与本地 API 服务 (127.0.0.1:17880)
        var webApp = AgentProxyHub.Host.HubHostFactory.BuildApp(hostArgs);
        _ = webApp.RunAsync();

        var mainForm = new MainForm("http://127.0.0.1:17880/", startMinimized);
        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateSignal,
            (_, _) =>
            {
                try
                {
                    if (!mainForm.IsDisposed && mainForm.IsHandleCreated)
                    {
                        mainForm.BeginInvoke(new Action(mainForm.ShowMainWindow));
                    }
                }
                catch { }
            },
            null,
            Timeout.Infinite,
            false);

        _shutdownWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _shutdownSignal,
            (_, _) =>
            {
                try
                {
                    if (!mainForm.IsDisposed && mainForm.IsHandleCreated)
                    {
                        mainForm.BeginInvoke(new Action(mainForm.ExitApplication));
                    }
                }
                catch { }
            },
            null,
            Timeout.Infinite,
            false);

        try
        {
            Application.Run(mainForm);
        }
        finally
        {
            try { _activateWaitHandle?.Unregister(null); } catch { }
            try { _shutdownWaitHandle?.Unregister(null); } catch { }
            try { _activateSignal?.Dispose(); } catch { }
            try { _shutdownSignal?.Dispose(); } catch { }
            try { webApp.StopAsync().GetAwaiter().GetResult(); } catch { }
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private static void TrySignalExistingShutdownAndWait()
    {
        try
        {
            using var existingShutdown = EventWaitHandle.OpenExisting(ShutdownEventName);
            existingShutdown.Set();
        }
        catch
        {
            // 若旧版进程没有命名退出信号，尝试直接继续，交由后续覆盖互斥锁检测处理
            return;
        }

        // 等待正在运行的实例释放单实例互斥锁，确保安装器覆盖文件时不发生占用冲突
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500)
        {
            try
            {
                using var m = new Mutex(false, SingleInstanceMutexName, out bool created);
                if (created)
                {
                    m.ReleaseMutex();
                    break;
                }
                if (m.WaitOne(120))
                {
                    m.ReleaseMutex();
                    break;
                }
            }
            catch (AbandonedMutexException)
            {
                break;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }
    }
}
