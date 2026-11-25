namespace AgentProxyHub.Desktop.Win;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(true, "AgentProxyHub_SingleInstance_Lock", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Agent Proxy Hub 已在系统托盘中运行。", "Agent Proxy Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // 在桌面端同进程后台拉起跨平台核心代理与本地 API 服务 (127.0.0.1:17880)
        var webApp = AgentProxyHub.Host.HubHostFactory.BuildApp(args);
        _ = webApp.RunAsync();

        try
        {
            Application.Run(new MainForm("http://127.0.0.1:17880/"));
        }
        finally
        {
            try { webApp.StopAsync().GetAwaiter().GetResult(); } catch { }
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }
}
