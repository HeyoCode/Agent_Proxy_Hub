using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentProxyHub.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentProxyHub.Desktop.Win;

/// <summary>
/// Windows 系统托盘原生无边框圆角快捷面板（WebView2 预热承载 ?tray=1）
/// 彻底取代传统灰底 ContextMenuStrip，左键/右键托盘图标即在光标附近优雅弹出。
/// </summary>
public sealed class TrayFlyoutForm : Form
{
    private const int LogicalWidth = 384;
    private const int MinLogicalHeight = 340;
    private const int MaxLogicalHeight = 620;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;

    private readonly Action _onOpenMain;
    private readonly Action _onExitApp;
    private readonly Action? _onToggleMini;
    private readonly WebView2 _webView;

    private int _logicalHeight = 468;
    private bool _warmedUp;
    private bool _suppressActivation;
    private bool _anchorBottom = true;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override bool ShowWithoutActivation => _suppressActivation;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            const int CsDropShadow = 0x00020000;

            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    public TrayFlyoutForm(Action onOpenMain, Action onExitApp, Action? onToggleMini = null)
    {
        _onOpenMain = onOpenMain;
        _onExitApp = onExitApp;
        _onToggleMini = onToggleMini;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;
        Width = ScalePx(LogicalWidth);
        Height = ScalePx(_logicalHeight);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.White
        };
        Controls.Add(_webView);

        Deactivate += (_, _) =>
        {
            if (!_suppressActivation && Visible)
            {
                _lastDeactivatedUtc = DateTime.UtcNow;
                Hide();
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwmRoundStyle();
    }

    private void ApplyDwmRoundStyle()
    {
        if (!IsHandleCreated) return;
        try
        {
            int corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            // 细腻淡冷灰描边 (COLORREF 0x00BBGGRR)，在浅色与深色桌面壁纸上都边界清晰优雅
            int borderColor = 0x00EBE2DE;
            _ = DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// 复用主窗口 CoreWebView2Environment 在屏幕外无感预热，保证首次点击托盘毫秒级弹出。
    /// </summary>
    public async Task WarmUpAsync(CoreWebView2Environment sharedEnv, string trayUrl)
    {
        if (_warmedUp || IsDisposed) return;

        try
        {
            _suppressActivation = true;
            Opacity = 0;
            Location = new Point(-32000, -32000);
            Show();

            await _webView.EnsureCoreWebView2Async(sharedEnv);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.Source = new Uri(trayUrl);

            _warmedUp = true;
        }
        catch
        {
            // 若 WebView2 预热异常不影响主窗口运行
        }
        finally
        {
            if (!IsDisposed)
            {
                Hide();
                Opacity = 1;
            }
            _suppressActivation = false;
        }
    }

    public void ToggleAtCursor()
    {
        if (IsDisposed || !_warmedUp) return;

        if (Visible)
        {
            HideFlyout();
            return;
        }

        // 防抖：点击托盘图标本身会触发一次 Deactivate，避免刚失焦关闭又瞬间重开
        if ((DateTime.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < 240)
        {
            return;
        }

        Point cursor = Cursor.Position;
        Rectangle workArea = Screen.FromPoint(cursor).WorkingArea;

        int targetW = ScalePx(LogicalWidth);
        int targetH = ScalePx(_logicalHeight);

        int x = Math.Clamp(cursor.X - targetW / 2, workArea.Left + 10, workArea.Right - targetW - 10);
        _anchorBottom = cursor.Y >= workArea.Top + workArea.Height / 2;

        int y = _anchorBottom
            ? Math.Max(workArea.Top + 10, workArea.Bottom - targetH - 10)
            : Math.Min(workArea.Bottom - targetH - 10, workArea.Top + 10);

        _suppressActivation = false;
        Bounds = new Rectangle(x, y, targetW, targetH);
        ApplyDwmRoundStyle();

        NotifyTrayShown();
        Show();
        Activate();
        _ = SetForegroundWindow(Handle);
    }

    public void HideFlyout()
    {
        if (!IsDisposed && Visible)
        {
            Hide();
        }
    }

    private void NotifyTrayShown()
    {
        try
        {
            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"tray:shown\"}");
            }
        }
        catch { }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            if (!json.RootElement.TryGetProperty("type", out var typeProp))
                return;

            switch (typeProp.GetString())
            {
                case "tray:openMain":
                    BeginInvoke(() =>
                    {
                        HideFlyout();
                        _onOpenMain();
                    });
                    break;

                case "tray:hide":
                    BeginInvoke(HideFlyout);
                    break;

                case "tray:exitApp":
                    BeginInvoke(() =>
                    {
                        HideFlyout();
                        _onExitApp();
                    });
                    break;

                case "tray:toggleMini":
                    BeginInvoke(() =>
                    {
                        HideFlyout();
                        _onToggleMini?.Invoke();
                    });
                    break;

                case "tray:resize":
                    if (json.RootElement.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out int hVal))
                    {
                        int clamped = Math.Clamp(hVal, MinLogicalHeight, MaxLogicalHeight);
                        BeginInvoke(() => UpdateDynamicHeight(clamped));
                    }
                    break;

                case "window:openUrl":
                    string? url = json.RootElement.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "https" or "http")
                    {
                        Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
                    }
                    break;
            }
        }
        catch { }
    }

    private void UpdateDynamicHeight(int logicalHeight)
    {
        _logicalHeight = logicalHeight;
        int newW = ScalePx(LogicalWidth);
        int newH = ScalePx(_logicalHeight);
        if (Size.Width == newW && Size.Height == newH) return;

        if (Visible)
        {
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            int newY = _anchorBottom
                ? Math.Max(wa.Top + 10, Bottom - newH)
                : Top;
            Bounds = new Rectangle(Left, newY, newW, newH);
        }
        else
        {
            Size = new Size(newW, newH);
        }
    }

    private int ScalePx(int logicalPx)
    {
        double scale = DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0;
        return (int)Math.Round(logicalPx * scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
