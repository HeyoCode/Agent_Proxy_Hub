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

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private readonly string _url;
    private readonly WebView2 _webView;
    private readonly Panel _startupCover;
    private readonly NotifyIcon _trayIcon;
    private TrayFlyoutForm? _trayFlyout;
    private FloatingMiniForm? _floatingMini;
    private bool _reallyExiting;
    private bool _isCustomMaximized;
    private bool _coverDismissed;
    private Rectangle _restoreBounds;
    private string _closeAction = "minimize_to_tray";
    private string _trayLeftClickAction = "toggle_flyout";
    private bool _miniCardEnabled;
    private int? _miniCardX;
    private int? _miniCardY;

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
        bool isDark = IsWindowsDarkMode();
        Color initBg = isDark ? Color.FromArgb(13, 16, 33) : Color.FromArgb(242, 244, 249);
        Color initFg = isDark ? Color.FromArgb(232, 236, 247) : Color.FromArgb(25, 32, 56);
        Color initMuted = isDark ? Color.FromArgb(134, 145, 179) : Color.FromArgb(100, 111, 142);

        Text = $"{ProjectMetadata.Name} v{ProjectMetadata.Version}";
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        Width = 1360;
        Height = 850;
        MinimumSize = new Size(1080, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = initBg;
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
            DefaultBackgroundColor = initBg
        };
        Controls.Add(_webView);

        // 原生双缓冲启动过渡层：WebView2 引擎和首帧完成合成前铺满窗口，彻底消除黑屏等待感
        _startupCover = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = initBg
        };
        _startupCover.Paint += (_, pe) =>
        {
            var g = pe.Graphics;
            g.Clear(initBg);
            using var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
            using var subFont = new Font("Segoe UI", 10f, FontStyle.Regular);
            using var fgBrush = new SolidBrush(initFg);
            using var mutedBrush = new SolidBrush(initMuted);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            float cx = _startupCover.Width / 2f;
            float cy = _startupCover.Height / 2f;
            g.DrawString($"{ProjectMetadata.Name} v{ProjectMetadata.Version}", titleFont, fgBrush, cx, cy - 14, sf);
            g.DrawString("正在加载物理网卡直出引擎...", subFont, mutedBrush, cx, cy + 16, sf);
        };
        Controls.Add(_startupCover);
        _startupCover.BringToFront();

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Shield,
            Text = $"{ProjectMetadata.Name} v{ProjectMetadata.Version} · 物理直出独立代理",
            Visible = true
        };
        // 左键按偏好响应（快捷面板/拉起主窗/悬浮监控窗），右键固定弹出托盘快捷面板
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _trayFlyout?.ToggleAtCursor();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                switch (_trayLeftClickAction)
                {
                    case "show_main":
                        _trayFlyout?.HideFlyout();
                        ShowMainWindow();
                        break;
                    case "toggle_mini":
                        ToggleMiniCard();
                        break;
                    default:
                        if (_trayFlyout != null)
                            _trayFlyout.ToggleAtCursor();
                        else
                            ShowMainWindow();
                        break;
                }
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
            // Windows 11 启用系统圆角（最大化时关闭圆角）、深色标题栏属性并隐藏 1px 外边框描边
            int darkAttr = IsWindowsDarkMode() ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref darkAttr, sizeof(int));

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

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return false;
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

            long buildTicks = 0;
            try
            {
                buildTicks = File.GetLastWriteTimeUtc(Application.ExecutablePath).Ticks;
            }
            catch { }

            // 仅当软件版本或构建时间戳发生变化时清理旧缓存；日常冷启动 0ms 保留 V8 字节码与静态缓存
            string stampPath = Path.Combine(webViewUserData, ".cache-stamp");
            string expectedStamp = $"{ProjectMetadata.Version}:{buildTicks}";
            try
            {
                string? existingStamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;
                if (!string.Equals(existingStamp, expectedStamp, StringComparison.Ordinal))
                {
                    if (_webView.CoreWebView2.Profile != null)
                    {
                        await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                            CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
                    }
                    File.WriteAllText(stampPath, expectedStamp);
                }
            }
            catch { }

            await WaitLocalBackendReadyAsync();

            var sep = _url.Contains('?') ? "&" : "?";
            string navUrl = $"{_url}{sep}v={Uri.EscapeDataString(ProjectMetadata.Version)}&b={buildTicks}";
            string trayUrl = $"{_url}{sep}tray=1&v={Uri.EscapeDataString(ProjectMetadata.Version)}&b={buildTicks}";
            string miniUrl = $"{_url}{sep}mini=1&v={Uri.EscapeDataString(ProjectMetadata.Version)}&b={buildTicks}";

            _trayFlyout = new TrayFlyoutForm(ShowMainWindow, ExitApplication, ToggleMiniCard);
            _floatingMini = new FloatingMiniForm(ShowMainWindow, _url);

            void DismissCover()
            {
                if (_coverDismissed) return;
                _coverDismissed = true;
                try
                {
                    if (!_startupCover.IsDisposed)
                    {
                        Controls.Remove(_startupCover);
                        _startupCover.Dispose();
                    }
                }
                catch { }
            }

            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                BeginInvoke(() =>
                {
                    DismissCover();
                    NotifyMaximizedState();
                });

                // 延迟到主界面首屏绘制完成之后再后台预热托盘面板与悬浮窗，避免与首屏抢占 CPU / GPU
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    if (IsDisposed) return;
                    BeginInvoke(async () =>
                    {
                        await SyncDesktopPreferencesAsync();
                        _ = _trayFlyout.WarmUpAsync(env, trayUrl);
                        _ = _floatingMini.WarmUpAsync(env, miniUrl);
                    });
                });
            };

            // 兜底安全网：即便极端情况未收到 NavigationCompleted，2.5s 后也确保移除过渡遮罩
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                if (!IsDisposed && !_coverDismissed)
                    BeginInvoke(DismissCover);
            });

            _webView.Source = new Uri(navUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败，请确认系统已安装 Edge WebView2 Runtime。\n详情: {ex.Message}",
                ProjectMetadata.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task WaitLocalBackendReadyAsync()
    {
        // 显式绕过系统代理并探测零开销 /api/health，彻底避免首次启动命中 Clash 代理或全进程扫描超时
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        string probeUrl = _url.TrimEnd('/') + "/api/health";
        for (int i = 0; i < 120; i++)
        {
            try
            {
                using var resp = await http.GetAsync(probeUrl);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(35);
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
                    BeginInvoke(() =>
                    {
                        if (_closeAction == "exit") ExitApplication();
                        else Hide();
                    });
                    break;

                case "window:toggleMini":
                    BeginInvoke(ToggleMiniCard);
                    break;

                case "window:preferencesSync":
                    BeginInvoke(async () => await SyncDesktopPreferencesAsync());
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
        _floatingMini?.HideMini();
        _trayIcon.Visible = false;
        Close();
    }

    private void ToggleMiniCard()
    {
        _miniCardEnabled = !_miniCardEnabled;
        if (_miniCardEnabled)
        {
            _floatingMini?.ShowMini(_miniCardX, _miniCardY);
        }
        else
        {
            _floatingMini?.HideMini();
        }
        _ = PersistMiniStateAsync(_miniCardEnabled);
    }

    private async Task PersistMiniStateAsync(bool enabled)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string payload = JsonSerializer.Serialize(new { miniCardEnabled = enabled });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            await http.PutAsync(_url.TrimEnd('/') + "/api/preferences", content);
        }
        catch { }
    }

    private async Task SyncDesktopPreferencesAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string json = await http.GetStringAsync(_url.TrimEnd('/') + "/api/preferences");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("closeAction", out var ca) && ca.ValueKind == JsonValueKind.String)
                _closeAction = ca.GetString() ?? "minimize_to_tray";
            if (root.TryGetProperty("trayLeftClickAction", out var ta) && ta.ValueKind == JsonValueKind.String)
                _trayLeftClickAction = ta.GetString() ?? "toggle_flyout";
            if (root.TryGetProperty("miniCardX", out var mx) && mx.ValueKind == JsonValueKind.Number)
                _miniCardX = mx.GetInt32();
            if (root.TryGetProperty("miniCardY", out var my) && my.ValueKind == JsonValueKind.Number)
                _miniCardY = my.GetInt32();
            if (root.TryGetProperty("miniCardEnabled", out var me) && me.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                bool wantMini = me.GetBoolean();
                _miniCardEnabled = wantMini;
                if (wantMini) _floatingMini?.ShowMini(_miniCardX, _miniCardY);
                else _floatingMini?.HideMini();
            }
        }
        catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExiting && e.CloseReason == CloseReason.UserClosing && _closeAction != "exit")
        {
            // 按桌面偏好决定关闭按钮行为：默认最小化至系统托盘，确保后台 AI 专线持续在线不中断
            e.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _floatingMini?.Dispose();
            _trayFlyout?.Dispose();
            _trayIcon.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
