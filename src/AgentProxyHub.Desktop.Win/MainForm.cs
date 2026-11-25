using System.Drawing;
using System.Net.Http;
using Microsoft.Web.WebView2.WinForms;

namespace AgentProxyHub.Desktop.Win;

/// <summary>
/// Windows 轻量原生桌面窗口 + 系统托盘常驻控制（WebView2 渲染 Cult UI 前端）
/// </summary>
public sealed class MainForm : Form
{
    private readonly string _url;
    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private bool _reallyExiting;

    public MainForm(string url)
    {
        _url = url;
        Text = "Agent Proxy Hub — AI 专用独立出口与用量网关";
        Width = 1380;
        Height = 880;
        MinimumSize = new Size(1060, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 10, 13);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主控制台", null, (_, _) => ShowMainWindow());
        menu.Items.Add("立即同步 AI 用量统计", null, async (_, _) =>
        {
            try
            {
                using var http = new HttpClient();
                await http.PostAsync("http://127.0.0.1:17880/api/stats/sync", null);
                _trayIcon?.ShowBalloonTip(1800, "Agent Proxy Hub", "已完成本地 AI 会话用量增量同步", ToolTipIcon.Info);
            }
            catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("彻底退出（停止独立代理）", null, (_, _) =>
        {
            _reallyExiting = true;
            _trayIcon!.Visible = false;
            Close();
        });

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Agent Proxy Hub · 物理直出独立代理运行中",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        Load += async (_, _) => await InitWebViewAsync();
        FormClosing += OnFormClosing;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败，请确认系统已安装 Edge WebView2 Runtime。\n详情: {ex.Message}",
                "Agent Proxy Hub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExiting && e.CloseReason == CloseReason.UserClosing)
        {
            // 关闭主窗口时默认收缩到系统托盘，确保后台 AI 专线持续在线不中断
            e.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
