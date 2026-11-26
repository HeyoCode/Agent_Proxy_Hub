using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentProxyHub.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentProxyHub.Desktop.Win;

/// <summary>
/// Windows 原生无边框圆角桌面窗口 + 系统托盘常驻控制（WebView2 渲染三栏雾白/午夜深蓝界面）
/// </summary>
public sealed class MainForm : Form
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private readonly string _url;
    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private TrayFlyoutForm? _trayFlyout;
    private bool _reallyExiting;
    private bool _isCustomMaximized;
    private Rectangle _restoreBounds;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsMinimizeBox = 0x00020000;
            const int WsSysMenu = 0x00080000;
            const int CsDropShadow = 0x00020000;

            var cp = base.CreateParams;
            cp.Style |= WsMinimizeBox | WsSysMenu;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    public MainForm(string url, bool startMinimized = false)
    {
        _url = url;
        Text = $"{ProjectMetadata.Name} v{ProjectMetadata.Version}";
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        Width = 1360;
        Height = 850;
        MinimumSize = new Size(1080, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(242, 244, 249);
        _restoreBounds = new Rectangle(120, 80, 1360, 850);

        Icon? appIcon = TryLoadAppIcon();
        if (appIcon != null) Icon = appIcon;

        if (startMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
        }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(242, 244, 249)
        };
        Controls.Add(_webView);

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Shield,
            Text = $"{ProjectMetadata.Name} v{ProjectMetadata.Version} · 物理直出独立代理",
            Visible = true
        };
        // 左键或右键单击托盘图标，均弹出系统级无边框圆角快捷面板，彻底替代传统 ContextMenuStrip
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
            {
                if (_trayFlyout != null)
                    _trayFlyout.ToggleAtCursor();
                else if (e.Button == MouseButtons.Left)
                    ShowMainWindow();
            }
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            _trayFlyout?.HideFlyout();
            ShowMainWindow();
        };

        Load += async (_, _) => await InitWebViewAsync();
        Shown += (_, _) =>
        {
            if (startMinimized) Hide();
            else ApplyFramelessDwmAttributes();
        };
        FormClosing += OnFormClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFramelessDwmAttributes();
    }

    private void ApplyFramelessDwmAttributes()
    {
        if (!IsHandleCreated) return;
        try
        {
            // Windows 11 启用系统圆角（最大化时关闭圆角）并彻底隐藏 1px 外边框描边
            int corner = _isCustomMaximized ? DwmwcpDoNotRound : DwmwcpRound;
            _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            int borderColor = DwmwaColorNone;
            _ = DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        }
        catch
        {
            // Windows 10 等旧版本自动忽略未知 DWM 属性，退回到 CS_DROPSHADOW
        }
    }

    private static Icon? TryLoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // 将 WebView2 用户数据迁出安装目录至 %LocalAppData%\AgentProxyHub\WebView2，避免卸载或覆盖污染安装目录
            string webViewUserData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentProxyHub",
                "WebView2");
            Directory.CreateDirectory(webViewUserData);

            var env = await CoreWebView2Environment.CreateAsync(null, webViewUserData);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.NewWindowRequested += OnWebViewNewWindowRequested;
            _webView.CoreWebView2.WebMessageReceived += OnWebViewMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, _) => NotifyMaximizedState();

            // 每次启动清理旧 HTTP 磁盘缓存，防止版本更新后命中残留 HTML / JS
            try
            {
                if (_webView.CoreWebView2.Profile != null)
                {
                    await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
                }
            }
            catch { }

            await WaitLocalBackendReadyAsync();

            long buildTicks = 0;
            try
            {
                buildTicks = File.GetLastWriteTimeUtc(Application.ExecutablePath).Ticks;
            }
            catch { }

            var sep = _url.Contains('?') ? "&" : "?";
            string navUrl = $"{_url}{sep}v={Uri.EscapeDataString(ProjectMetadata.Version)}&b={buildTicks}";
            _webView.Source = new Uri(navUrl);

            // 后台无感预热托盘快捷面板（复用共享 WebView2 环境，首击毫秒级呼出）
            string trayUrl = $"{_url}{sep}tray=1&v={Uri.EscapeDataString(ProjectMetadata.Version)}&b={buildTicks}";
            _trayFlyout = new TrayFlyoutForm(ShowMainWindow, ExitApplication);
            _ = _trayFlyout.WarmUpAsync(env, trayUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败，请确认系统已安装 Edge WebView2 Runtime。\n详情: {ex.Message}",
                ProjectMetadata.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task WaitLocalBackendReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
        string probeUrl = _url.TrimEnd('/') + "/api/overview";
        for (int i = 0; i < 25; i++)
        {
            try
            {
                using var resp = await http.GetAsync(probeUrl);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(100);
        }
    }

    private void OnWebViewMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            if (!json.RootElement.TryGetProperty("type", out var typeProp))
                return;

            string? type = typeProp.GetString();
            switch (type)
            {
                case "window:drag":
                    BeginInvoke(StartNativeDrag);
                    break;

                case "window:resize":
                    string? edge = json.RootElement.TryGetProperty("edge", out var edgeProp)
                        ? edgeProp.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(edge))
                    {
                        BeginInvoke(() => StartNativeResize(edge));
                    }
                    break;

                case "window:minimize":
                    BeginInvoke(() => WindowState = FormWindowState.Minimized);
                    break;

                case "window:toggleMaximize":
                    BeginInvoke(ToggleCustomMaximize);
                    break;

                case "window:hideToTray":
                    BeginInvoke(Hide);
                    break;

                case "window:openUrl":
                    string? url = json.RootElement.TryGetProperty("url", out var urlProp)
                        ? urlProp.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        OpenUrlInBrowser(url);
                    }
                    break;
            }
        }
        catch { }
    }

    private void StartNativeDrag()
    {
        if (_isCustomMaximized)
        {
            // 最大化状态下开始拖拽时，按指针相对位置平滑还原为正常窗口再跟随移动
            var cursor = Cursor.Position;
            var workArea = Screen.FromPoint(cursor).WorkingArea;
            double ratioX = workArea.Width > 0 ? Math.Clamp((cursor.X - workArea.X) / (double)workArea.Width, 0.12, 0.88) : 0.5;
            int w = _restoreBounds.Width >= MinimumSize.Width ? _restoreBounds.Width : 1360;
            int h = _restoreBounds.Height >= MinimumSize.Height ? _restoreBounds.Height : 850;
            int newX = Math.Clamp((int)(cursor.X - w * ratioX), workArea.Left - 80, workArea.Right - 220);
            int newY = Math.Max(workArea.Top, cursor.Y - 26);
            _isCustomMaximized = false;
            Bounds = new Rectangle(newX, newY, w, h);
            ApplyFramelessDwmAttributes();
            NotifyMaximizedState();
        }

        ReleaseCapture();
        _ = SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void StartNativeResize(string edge)
    {
        if (_isCustomMaximized) return;

        int hitTest = edge switch
        {
            "n" => HtTop,
            "s" => HtBottom,
            "w" => HtLeft,
            "e" => HtRight,
            "nw" => HtTopLeft,
            "ne" => HtTopRight,
            "sw" => HtBottomLeft,
            "se" => HtBottomRight,
            _ => 0
        };

        if (hitTest == 0) return;
        ReleaseCapture();
        _ = SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitTest, IntPtr.Zero);
    }

    private void ToggleCustomMaximize()
    {
        if (!_isCustomMaximized)
        {
            _restoreBounds = Bounds;
            _isCustomMaximized = true;
            // 限定至 WorkingArea 铺满可用桌面空间，绝不遮挡 Windows 任务栏
            Bounds = Screen.FromHandle(Handle).WorkingArea;
        }
        else
        {
            _isCustomMaximized = false;
            if (_restoreBounds.Width >= MinimumSize.Width && _restoreBounds.Height >= MinimumSize.Height)
            {
                Bounds = _restoreBounds;
            }
            else
            {
                Size = new Size(1360, 850);
                CenterToScreen();
            }
        }

        ApplyFramelessDwmAttributes();
        NotifyMaximizedState();
    }

    private void NotifyMaximizedState()
    {
        try
        {
            if (_webView.CoreWebView2 == null) return;
            string payload = JsonSerializer.Serialize(new { type = "window:maximizedState", maximized = _isCustomMaximized });
            _webView.CoreWebView2.PostWebMessageAsJson(payload);
        }
        catch { }
    }

    private static void OnWebViewNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                OpenUrlInBrowser(ProjectMetadata.RepoUrl);
            }
        }
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void ShowMainWindow()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        ApplyFramelessDwmAttributes();
        Activate();
    }

    public void ExitApplication()
    {
        _reallyExiting = true;
        _trayFlyout?.HideFlyout();
        _trayIcon.Visible = false;
        Close();
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
            _trayFlyout?.Dispose();
            _trayIcon.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
