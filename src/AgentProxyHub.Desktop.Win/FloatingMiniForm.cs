using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentProxyHub.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentProxyHub.Desktop.Win;

/// <summary>
/// 始终置顶（Always-on-Top）极简悬浮监控卡片（显示实时活跃席位、吞吐与快捷开关）
/// </summary>
public sealed class FloatingMiniForm : Form
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int LogicalWidth = 284;

    private readonly Action _showMainWindow;
    private readonly string _apiBaseUrl;
    private readonly WebView2 _webView;
    private bool _initialized;
    private bool _desiredVisible;
    private string? _pendingUrl;
    private int _logicalHeight = 136;

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
            const int WsExToolWindow = 0x00000080;
            const int CsDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    public FloatingMiniForm(Action showMainWindow, string apiBaseUrl)
    {
        _showMainWindow = showMainWindow;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(242, 244, 249);
        Size = new Size(LogicalWidth, _logicalHeight);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(242, 244, 249)
        };
        Controls.Add(_webView);

        Move += (_, _) => PersistPositionDebounced();
    }

    public async Task WarmUpAsync(CoreWebView2Environment env, string miniUrl)
    {
        if (_initialized) return;
        _pendingUrl = miniUrl;

        try
        {
            _ = Handle;
            ApplyDwmStyle();
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, _) => NotifyMiniVisibility(_desiredVisible || Visible);
            _webView.Source = new Uri(_pendingUrl);
            _initialized = true;
        }
        catch { }
    }

    public void ShowMini(int? savedX, int? savedY)
    {
        _desiredVisible = true;
        if (!IsHandleCreated) _ = Handle;
        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        int w = ScalePx(LogicalWidth);
        int h = ScalePx(_logicalHeight);

        int x = savedX.HasValue
            ? Math.Clamp(savedX.Value, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8))
            : wa.Right - w - 20;
        int y = savedY.HasValue
            ? Math.Clamp(savedY.Value, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - h - 8))
            : wa.Top + 20;

        Bounds = new Rectangle(x, y, w, h);
        ApplyDwmStyle();
        Show();
        TopMost = true;
        NotifyMiniVisibility(true);
    }

    public void HideMini()
    {
        _desiredVisible = false;
        NotifyMiniVisibility(false);
        if (Visible) Hide();
    }

    private void NotifyMiniVisibility(bool visible)
    {
        try
        {
            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    visible ? "{\"type\":\"mini:shown\"}" : "{\"type\":\"mini:hidden\"}");
            }
        }
        catch { }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            if (!json.RootElement.TryGetProperty("type", out var typeProp)) return;
            switch (typeProp.GetString())
            {
                case "mini:drag":
                    BeginInvoke(() =>
                    {
                        ReleaseCapture();
                        _ = SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                        PersistPositionDebounced();
                    });
                    break;
                case "mini:openMain":
                    BeginInvoke(_showMainWindow);
                    break;
                case "mini:close":
                    BeginInvoke(() =>
                    {
                        HideMini();
                        _ = PersistPreferenceAsync(new { miniCardEnabled = false });
                    });
                    break;
                case "mini:resize":
                    if (json.RootElement.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out int hVal))
                    {
                        BeginInvoke(() =>
                        {
                            _logicalHeight = Math.Clamp(hVal, 108, 220);
                            Size = new Size(ScalePx(LogicalWidth), ScalePx(_logicalHeight));
                        });
                    }
                    break;
            }
        }
        catch { }
    }

    private void PersistPositionDebounced()
    {
        if (!Visible) return;
        int x = Left;
        int y = Top;
        _ = PersistPreferenceAsync(new { miniCardX = x, miniCardY = y });
    }

    private async Task PersistPreferenceAsync(object body)
    {
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await http.PutAsync($"{_apiBaseUrl}/api/preferences", content);
        }
        catch { }
    }

    private void ApplyDwmStyle()
    {
        if (!IsHandleCreated) return;
        try
        {
            int corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            int borderColor = DwmwaColorNone;
            _ = DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        }
        catch { }
    }

    private int ScalePx(int logicalPx)
    {
        double scale = DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0;
        return (int)Math.Round(logicalPx * scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _webView.Dispose();
        base.Dispose(disposing);
    }
}
