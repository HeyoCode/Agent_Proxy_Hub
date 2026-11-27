using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Storage;

/// <summary>
/// 跨机器 WebDAV 加密同步服务：
/// - 在本地先使用口令派生密钥 (PBKDF2-SHA256) + AES-256-GCM 对配置与节点凭据整体加密
/// - 再上传到用户自托管 WebDAV 网盘（坚果云 / Nextcloud / Alist / Synology），服务端零明文暴露
/// </summary>
public sealed class WebDavSyncService
{
    private sealed class EncryptedEnvelope
    {
        public string Format { get; set; } = "agent-proxy-hub-webdav-v2";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; } = 100_000;
        public string Cipher { get; set; } = "AES-256-GCM";
        public string SaltBase64 { get; set; } = string.Empty;
        public string NonceBase64 { get; set; } = string.Empty;
        public string TagBase64 { get; set; } = string.Empty;
        public string CiphertextBase64 { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string EncryptDocument(HubStateDocument doc, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 4)
            throw new ArgumentException("同步加密口令长度至少 4 位，用于保护节点密码与订阅地址。");

        // 剥离 WebDAV 登录凭据本身，避免备份内嵌套主账号密码
        var sanitizedPref = doc.Preferences.ToRedactedCopy();
        sanitizedPref.WebDavPassword = null;
        sanitizedPref.WebDavSyncPassphrase = null;

        var payloadDoc = new HubStateDocument
        {
            Isolation = doc.Isolation,
            Nodes = doc.Nodes,
            Seats = doc.Seats,
            Instances = doc.Instances,
            Subscriptions = doc.Subscriptions,
            CustomPricings = doc.CustomPricings,
            Preferences = sanitizedPref,
            HostApiPort = doc.HostApiPort
        };

        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadDoc, JsonOpts));
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var envelope = new EncryptedEnvelope
        {
            SaltBase64 = Convert.ToBase64String(salt),
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag),
            CiphertextBase64 = Convert.ToBase64String(ciphertext)
        };
        return JsonSerializer.Serialize(envelope, JsonOpts);
    }

    public static HubStateDocument DecryptDocument(string encryptedPayload, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("请输入同步解密口令。");

        var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(encryptedPayload, JsonOpts)
            ?? throw new InvalidDataException("无效的 WebDAV 加密备份格式。");
        if (envelope.Format != "agent-proxy-hub-webdav-v2")
            throw new InvalidDataException($"不支持的备份版本: {envelope.Format}");

        byte[] salt = Convert.FromBase64String(envelope.SaltBase64);
        byte[] nonce = Convert.FromBase64String(envelope.NonceBase64);
        byte[] tag = Convert.FromBase64String(envelope.TagBase64);
        byte[] ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            envelope.Iterations > 0 ? envelope.Iterations : 100_000,
            HashAlgorithmName.SHA256,
            32);

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("解密失败：同步加密口令不正确或云端备份已损坏。");
        }

        return JsonSerializer.Deserialize<HubStateDocument>(Encoding.UTF8.GetString(plaintext), JsonOpts)
            ?? throw new InvalidDataException("解密后配置反序列化失败。");
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(
        string webDavUrl,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        try
        {
            string baseDir = NormalizeBaseUrl(webDavUrl);
            using var http = CreateClient(username, password);
            using var resp = await SendWithSameHostRedirectAsync(
                http,
                () =>
                {
                    var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseDir);
                    req.Headers.TryAddWithoutValidation("Depth", "0");
                    return req;
                },
                ct);
            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed || resp.StatusCode == HttpStatusCode.NotImplemented)
            {
                using var optResp = await SendWithSameHostRedirectAsync(
                    http,
                    () => new HttpRequestMessage(HttpMethod.Options, baseDir),
                    ct);
                return optResp.IsSuccessStatusCode
                    ? (true, $"WebDAV 连通正常 ({(int)optResp.StatusCode})")
                    : (false, $"WebDAV 返回 HTTP {(int)optResp.StatusCode}");
            }
            if ((int)resp.StatusCode is >= 200 and < 300 || resp.StatusCode == HttpStatusCode.MultiStatus)
                return (true, $"WebDAV 认证与目录可达 ({(int)resp.StatusCode})");

            return (false, $"WebDAV 连接失败: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, $"WebDAV 连接异常: {ex.Message}");
        }
    }

    public async Task<(bool Ok, string Message, DateTimeOffset? SyncedAt)> PushBackupAsync(
        HubStateDocument doc,
        string webDavUrl,
        string? username,
        string? password,
        string passphrase,
        CancellationToken ct = default)
    {
        try
        {
            string folderUrl = NormalizeBaseUrl(webDavUrl) + "agent-proxy-hub/";
            string fileUrl = folderUrl + "backup-latest.enc.json";
            string encrypted = EncryptDocument(doc, passphrase);

            using var http = CreateClient(username, password);
            // 尝试 MKCOL 确保子目录存在（已存在则返回 405 属正常）
            using (await SendWithSameHostRedirectAsync(http, () => new HttpRequestMessage(new HttpMethod("MKCOL"), folderUrl), ct)) { }

            using var putResp = await SendWithSameHostRedirectAsync(
                http,
                () => new HttpRequestMessage(HttpMethod.Put, fileUrl)
                {
                    Content = new StringContent(encrypted, Encoding.UTF8, "application/json")
                },
                ct);
            if (!putResp.IsSuccessStatusCode)
            {
                return (false, $"上传 WebDAV 失败: HTTP {(int)putResp.StatusCode} {putResp.ReasonPhrase}", null);
            }

            var syncedAt = DateTimeOffset.UtcNow;
            return (true, $"已通过 AES-256-GCM 加密上传 {doc.Nodes.Count} 条节点、{doc.Seats.Count} 席位与 {doc.Instances.Count} 个多开实例到云端", syncedAt);
        }
        catch (Exception ex)
        {
            return (false, $"上传 WebDAV 异常: {ex.Message}", null);
        }
    }

    public async Task<(bool Ok, string Message, HubStateDocument? Restored)> PullBackupAsync(
        string webDavUrl,
        string? username,
        string? password,
        string passphrase,
        CancellationToken ct = default)
    {
        try
        {
            string fileUrl = NormalizeBaseUrl(webDavUrl) + "agent-proxy-hub/backup-latest.enc.json";
            using var http = CreateClient(username, password);
            using var resp = await SendWithSameHostRedirectAsync(http, () => new HttpRequestMessage(HttpMethod.Get, fileUrl), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return (false, "WebDAV 云端尚未找到 backup-latest.enc.json，请先在原设备点击「加密上传」。", null);
            if (!resp.IsSuccessStatusCode)
                return (false, $"下载 WebDAV 备份失败: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", null);

            string raw = await resp.Content.ReadAsStringAsync(ct);
            var restored = DecryptDocument(raw, passphrase);
            return (true, $"已解密恢复 {restored.Nodes.Count} 条线路、{restored.Seats.Count} 个席位与 {restored.Instances.Count} 个实例", restored);
        }
        catch (Exception ex)
        {
            return (false, $"拉取/解密 WebDAV 备份失败: {ex.Message}", null);
        }
    }

    private static string NormalizeBaseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("WebDAV 地址必须是合法的 http:// 或 https:// URL。");
        }
        string clean = uri.ToString();
        return clean.EndsWith('/') ? clean : clean + "/";
    }

    /// <summary>
    /// 允许同主机路径或同主机 HTTP->HTTPS 跳转的重定向，拒绝跨域名重定向以防止 Basic 凭据泄露
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithSameHostRedirectAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        HttpRequestMessage currentReq = requestFactory();
        HttpResponseMessage resp = await http.SendAsync(currentReq, ct);

        for (int hop = 0; hop < 3; hop++)
        {
            int code = (int)resp.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308))
                return resp;

            Uri? baseUri = currentReq.RequestUri;
            Uri? location = resp.Headers.Location;
            if (baseUri == null || location == null)
                return resp;

            Uri resolved = location.IsAbsoluteUri ? location : new Uri(baseUri, location);
            bool sameHost = resolved.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase);
            bool safeScheme = resolved.Scheme == baseUri.Scheme ||
                              (baseUri.Scheme == Uri.UriSchemeHttp && resolved.Scheme == Uri.UriSchemeHttps);
            if (!sameHost || !safeScheme)
                return resp;

            var prevMethod = currentReq.Method;
            resp.Dispose();
            currentReq.Dispose();
            currentReq = requestFactory();
            if (code == 303)
            {
                currentReq.Method = HttpMethod.Get;
                currentReq.Content = null;
            }
            else
            {
                currentReq.Method = prevMethod;
            }
            currentReq.RequestUri = resolved;
            resp = await http.SendAsync(currentReq, ct);
        }

        return resp;
    }

    private static HttpClient CreateClient(string? username, string? password)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            string raw = $"{username.Trim()}:{password ?? string.Empty}";
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AgentProxyHub/{ProjectMetadata.Version}");
        return http;
    }
}
