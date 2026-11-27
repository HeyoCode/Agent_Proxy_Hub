using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentProxyHub.Core.Diagnostics;

namespace AgentProxyHub.Core.Storage;

/// <summary>
/// 零第三方依赖跨平台 AES-256-GCM 本地凭据保险库：
/// - 密钥文件 `.vault.key` 存储在配置目录下
/// - Windows 优先调用系统 DPAPI (CryptProtectData CurrentUser) 封装主密钥，非 Windows 设置 0600 严格权限
/// - 密文采用 `enc:v1:<base64(nonce12 || tag16 || cipher)>` 格式，天然兼容旧版明文迁移
/// </summary>
public sealed class CredentialVault
{
    public const string CipherPrefix = "enc:v1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyPath;
    private readonly object _keyLock = new();
    private byte[]? _cachedKey;

    public CredentialVault(string directory)
    {
        Directory.CreateDirectory(directory);
        _keyPath = Path.Combine(directory, ".vault.key");
    }

    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(CipherPrefix, StringComparison.Ordinal);

    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsEncrypted(plain))
            return plain;

        byte[] key = GetOrCreateMasterKey();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        byte[] tag = new byte[TagSize];
        byte[] cipher = new byte[plainBytes.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        byte[] combined = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, NonceSize + TagSize, cipher.Length);

        return CipherPrefix + Convert.ToBase64String(combined);
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!IsEncrypted(value)) return value;

        try
        {
            byte[] combined = Convert.FromBase64String(value[CipherPrefix.Length..]);
            if (combined.Length < NonceSize + TagSize) return string.Empty;

            // 只读解密绝不创建或覆盖已有 .vault.key，避免瞬态异常破坏历史凭据
            byte[]? key = TryGetExistingMasterKey();
            if (key == null) return string.Empty;

            var nonce = combined.AsSpan(0, NonceSize);
            var tag = combined.AsSpan(NonceSize, TagSize);
            var cipher = combined.AsSpan(NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // 密钥损坏或更换机器无法解封时安全降级为空串，阻止启动崩溃
            return string.Empty;
        }
    }

    private byte[]? TryGetExistingMasterKey()
    {
        lock (_keyLock)
        {
            if (_cachedKey != null) return _cachedKey;
            if (!File.Exists(_keyPath)) return null;

            if (TryReadKeyWithRetry(out byte[]? raw, out _) && raw is { Length: > 0 })
            {
                byte[]? unwrapped = UnwrapKeyBytes(raw);
                if (unwrapped is { Length: KeySize })
                {
                    _cachedKey = unwrapped;
                    return _cachedKey;
                }
            }
            return null;
        }
    }

    private byte[] GetOrCreateMasterKey()
    {
        lock (_keyLock)
        {
            if (_cachedKey != null) return _cachedKey;

            if (File.Exists(_keyPath))
            {
                if (!TryReadKeyWithRetry(out byte[]? raw, out Exception? ioEx))
                {
                    // 文件被临时占用或无权限时禁止生成新密钥覆盖，直接抛错保护现有密钥
                    throw new IOException($"无法读取已有凭据主密钥 {_keyPath}，已中止覆盖以保护历史密文。", ioEx);
                }

                if (raw is { Length: > 0 })
                {
                    byte[]? unwrapped = UnwrapKeyBytes(raw);
                    if (unwrapped is { Length: KeySize })
                    {
                        _cachedKey = unwrapped;
                        return _cachedKey;
                    }

                    // 已有密钥文件损坏或跨机 DPAPI 无法解封：先留存损坏备份再轮换，严禁静默丢失
                    string backupPath = $"{_keyPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                    try
                    {
                        File.Copy(_keyPath, backupPath, overwrite: true);
                        HubEventLogger.Default.Warn("GUARD", $"检测到凭据主密钥无法解封，已安全备份至 {Path.GetFileName(backupPath)} 后重新生成新密钥。");
                    }
                    catch { }
                }
            }

            byte[] key = new byte[KeySize];
            RandomNumberGenerator.Fill(key);
            byte[] toStore = WrapKeyBytes(key);
            string tmpPath = _keyPath + ".tmp";

            File.WriteAllBytes(tmpPath, toStore);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(tmpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // 忽略不支持 UnixFileMode 的文件系统
                }
            }
            File.Move(tmpPath, _keyPath, overwrite: true);

            _cachedKey = key;
            return _cachedKey;
        }
    }

    private bool TryReadKeyWithRetry(out byte[]? raw, out Exception? lastError)
    {
        raw = null;
        lastError = null;
        int[] delaysMs = [30, 80, 150];
        for (int attempt = 0; attempt <= delaysMs.Length; attempt++)
        {
            try
            {
                raw = File.ReadAllBytes(_keyPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < delaysMs.Length)
                {
                    Thread.Sleep(delaysMs[attempt]);
                }
            }
        }
        return false;
    }

    private static byte[] WrapKeyBytes(byte[] key)
    {
        if (OperatingSystem.IsWindows())
        {
            if (TryDpapiProtect(key, out byte[]? protectedBlob) && protectedBlob != null)
            {
                // 添加 4 字节魔数标识 DPAPI 封装
                byte[] res = new byte[4 + protectedBlob.Length];
                res[0] = (byte)'D';
                res[1] = (byte)'P';
                res[2] = (byte)'V';
                res[3] = (byte)'1';
                Buffer.BlockCopy(protectedBlob, 0, res, 4, protectedBlob.Length);
                return res;
            }
        }
        return key;
    }

    private static byte[]? UnwrapKeyBytes(byte[] raw)
    {
        if (raw.Length > 4 && raw[0] == (byte)'D' && raw[1] == (byte)'P' && raw[2] == (byte)'V' && raw[3] == (byte)'1')
        {
            if (!OperatingSystem.IsWindows()) return null;
            byte[] blob = raw[4..];
            return TryDpapiUnprotect(blob, out byte[]? plain) ? plain : null;
        }
        return raw.Length == KeySize ? raw : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static bool TryDpapiProtect(byte[] plain, out byte[]? protectedBytes)
    {
        protectedBytes = null;
        IntPtr inPtr = Marshal.AllocHGlobal(plain.Length);
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(plain, 0, inPtr, plain.Length);
            var inBlob = new DataBlob { cbData = plain.Length, pbData = inPtr };
            if (!CryptProtectData(ref inBlob, "AgentProxyHubVault", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                return false;

            protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, outBlob.cbData);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static bool TryDpapiUnprotect(byte[] protectedBytes, out byte[]? plainBytes)
    {
        plainBytes = null;
        IntPtr inPtr = Marshal.AllocHGlobal(protectedBytes.Length);
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(protectedBytes, 0, inPtr, protectedBytes.Length);
            var inBlob = new DataBlob { cbData = protectedBytes.Length, pbData = inPtr };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                return false;

            plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
