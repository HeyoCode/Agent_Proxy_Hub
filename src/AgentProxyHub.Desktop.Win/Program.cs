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

        // 桌面壳立即创建窗口，跨平台核心代理与本地 API 在后台线程并行初始化与端口绑定，消除启动阻塞黑屏
        Microsoft.AspNetCore.Builder.WebApplication? webApp = null;
        var hostReadyGate = new ManualResetEventSlim(false);
        _ = Task.Run(() =>
        {
            try
            {
                var built = AgentProxyHub.Host.HubHostFactory.BuildApp(hostArgs);
                webApp = built;
                hostReadyGate.Set();
                built.Run();
            }
            catch
            {
                hostReadyGate.Set();
            }
        });

        int apiPort = ResolveConfiguredApiPort(hostArgs);
        var mainForm = new MainForm($"http://127.0.0.1:{apiPort}/", startMinimized);
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
            try
            {
                hostReadyGate.Wait(TimeSpan.FromSeconds(2));
                if (webApp != null) webApp.StopAsync().GetAwaiter().GetResult();
            }
            catch { }
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private static int ResolveConfiguredApiPort(string[] hostArgs)
    {
        for (int i = 0; i < hostArgs.Length; i++)
        {
            string a = hostArgs[i];
            if (a.StartsWith("--verify-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a["--verify-port=".Length..], out int eqPort) &&
                eqPort is >= 1024 and <= 65535)
            {
                return eqPort;
            }
            if (a.Equals("--verify-port", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < hostArgs.Length &&
                int.TryParse(hostArgs[i + 1], out int nextPort) &&
                nextPort is >= 1024 and <= 65535)
            {
                return nextPort;
            }
        }

        try
        {
            string statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agent-proxy-hub",
                "state.json");
            if (File.Exists(statePath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("hostApiPort", out var prop) &&
                    prop.TryGetInt32(out int port) &&
                    port is >= 1024 and <= 65535)
                {
                    return port;
                }
            }
        }
        catch { }
        return 17880;
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
