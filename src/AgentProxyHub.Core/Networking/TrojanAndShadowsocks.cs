using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgentProxyHub.Core.Networking;

/// <summary>
/// 原生零依赖加密代理协议支持：
/// - Trojan (FIPS 180-4 SHA-224 + 标准请求头编码)
/// - Shadowsocks AEAD (SIP004: aes-256-gcm / aes-128-gcm / chacha20-ietf-poly1305)
/// </summary>
public static class NativeCryptoProtocols
{
    private static readonly uint[] Sha256K =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ];

    /// <summary>
    /// 标准 FIPS 180-4 SHA-224 计算（返回 56 位小写十六进制字符串，用于 Trojan 协议密码哈希）
    /// </summary>
    public static string ComputeSha224Hex(string input)
    {
        byte[] data = Encoding.UTF8.GetBytes(input);
        ulong bitLen = (ulong)data.Length * 8UL;
        int padZeroLen = (56 - (data.Length + 1) % 64 + 64) % 64;
        byte[] padded = new byte[data.Length + 1 + padZeroLen + 8];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8, 8), bitLen);

        uint h0 = 0xc1059ed8;
        uint h1 = 0x367cd507;
        uint h2 = 0x3070dd17;
        uint h3 = 0xf70e5939;
        uint h4 = 0xffc00b31;
        uint h5 = 0x68581511;
        uint h6 = 0x64f98fa7;
        uint h7 = 0xbefa4fa4;

        Span<uint> w = stackalloc uint[64];
        for (int offset = 0; offset < padded.Length; offset += 64)
        {
            for (int i = 0; i < 16; i++)
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(offset + i * 4, 4));
            for (int i = 16; i < 64; i++)
            {
                uint s0 = RotateRight(w[i - 15], 7) ^ RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = RotateRight(w[i - 2], 17) ^ RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
            for (int i = 0; i < 64; i++)
            {
                uint s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint temp1 = h + s1 + ch + Sha256K[i] + w[i];
                uint s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint temp2 = s0 + maj;

                h = g; g = f; f = e; e = d + temp1;
                d = c; c = b; b = a; a = temp1 + temp2;
            }

            h0 += a; h1 += b; h2 += c; h3 += d;
            h4 += e; h5 += f; h6 += g; h7 += h;
        }

        Span<byte> digest = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(digest[0..4], h0);
        BinaryPrimitives.WriteUInt32BigEndian(digest[4..8], h1);
        BinaryPrimitives.WriteUInt32BigEndian(digest[8..12], h2);
        BinaryPrimitives.WriteUInt32BigEndian(digest[12..16], h3);
        BinaryPrimitives.WriteUInt32BigEndian(digest[16..20], h4);
        BinaryPrimitives.WriteUInt32BigEndian(digest[20..24], h5);
        BinaryPrimitives.WriteUInt32BigEndian(digest[24..28], h6);
        return Convert.ToHexStringLower(digest);
    }

    private static uint RotateRight(uint v, int bits) => (v >> bits) | (v << (32 - bits));

    /// <summary>
    /// 编码 SOCKS5 标准目标地址头：[ATYP][ADDR][PORT_BE]（Trojan 与 Shadowsocks 共用）
    /// </summary>
    public static byte[] EncodeSocksAddress(string targetHost, int targetPort)
    {
        string cleanHost = targetHost.Trim().Trim('[', ']');
        using var ms = new MemoryStream(64);
        if (IPAddress.TryParse(cleanHost, out var ip))
        {
            byte[] ipBytes = ip.GetAddressBytes();
            ms.WriteByte(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04);
            ms.Write(ipBytes);
        }
        else
        {
            byte[] hostBytes = Encoding.ASCII.GetBytes(cleanHost);
            if (hostBytes.Length > 255) throw new InvalidOperationException("目标域名长度超过 255 字节");
            ms.WriteByte(0x03);
            ms.WriteByte((byte)hostBytes.Length);
            ms.Write(hostBytes);
        }

        byte[] portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)targetPort);
        ms.Write(portBytes);
        return ms.ToArray();
    }

    /// <summary>
    /// 构造标准 Trojan CONNECT 请求首包：[SHA224(pass)]\r\n[0x01][ATYP][ADDR][PORT]\r\n
    /// </summary>
    public static byte[] BuildTrojanConnectHeader(string password, string targetHost, int targetPort)
    {
        byte[] hashBytes = Encoding.ASCII.GetBytes(ComputeSha224Hex(password));
        byte[] addrBytes = EncodeSocksAddress(targetHost, targetPort);

        byte[] header = new byte[hashBytes.Length + 2 + 1 + addrBytes.Length + 2];
        int pos = 0;
        Buffer.BlockCopy(hashBytes, 0, header, pos, hashBytes.Length); pos += hashBytes.Length;
        header[pos++] = (byte)'\r';
        header[pos++] = (byte)'\n';
        header[pos++] = 0x01; // CMD = CONNECT
        Buffer.BlockCopy(addrBytes, 0, header, pos, addrBytes.Length); pos += addrBytes.Length;
        header[pos++] = (byte)'\r';
        header[pos++] = (byte)'\n';
        return header;
    }

    /// <summary>
    /// Shadowsocks OpenSSL EVP_BytesToKey (MD5) 主密钥派生
    /// </summary>
    public static byte[] EvpBytesToKey(string password, int keyLen)
    {
        byte[] passBytes = Encoding.UTF8.GetBytes(password);
        byte[] result = new byte[keyLen];
        byte[] prev = [];
        int offset = 0;

        while (offset < keyLen)
        {
            byte[] input = new byte[prev.Length + passBytes.Length];
            Buffer.BlockCopy(prev, 0, input, 0, prev.Length);
            Buffer.BlockCopy(passBytes, 0, input, prev.Length, passBytes.Length);
            prev = MD5.HashData(input);
            int copy = Math.Min(prev.Length, keyLen - offset);
            Buffer.BlockCopy(prev, 0, result, offset, copy);
            offset += copy;
        }
        return result;
    }
}

/// <summary>
/// Shadowsocks AEAD 全双工加密流（原生支持 aes-256-gcm / aes-128-gcm / chacha20-ietf-poly1305）
/// </summary>
public sealed class ShadowsocksAeadStream : Stream
{
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int MaxPayloadChunk = 0x3FFF; // 16383 bytes per SIP004
    private static readonly byte[] HkdfInfo = "ss-subkey"u8.ToArray();

    private enum CipherKind
    {
        Aes256Gcm,
        Aes128Gcm,
        ChaCha20Poly1305
    }

    private readonly Stream _inner;
    private readonly CipherKind _cipherKind;
    private readonly int _keySize;
    private readonly int _saltSize;
    private readonly byte[] _masterKey;

    private bool _writeSaltSent;
    private byte[]? _writeSubkey;
    private IDisposable? _writeCipher;
    private readonly byte[] _writeNonce = new byte[NonceSize];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private bool _readSaltReceived;
    private byte[]? _readSubkey;
    private IDisposable? _readCipher;
    private readonly byte[] _readNonce = new byte[NonceSize];
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private byte[] _readPlainBuffer = [];
    private int _readPlainOffset;
    private int _readPlainCount;

    public ShadowsocksAeadStream(Stream inner, string method, string password)
    {
        _inner = inner;
        string m = (method ?? string.Empty).Trim().ToLowerInvariant();
        (_cipherKind, _keySize, _saltSize) = m switch
        {
            "aes-256-gcm" => (CipherKind.Aes256Gcm, 32, 32),
            "aes-128-gcm" => (CipherKind.Aes128Gcm, 16, 16),
            "chacha20-ietf-poly1305" or "chacha20-poly1305" => (CipherKind.ChaCha20Poly1305, 32, 32),
            _ => throw new NotSupportedException($"Shadowsocks 原生引擎暂不支持加密算法 '{method}'（支持 aes-256-gcm / aes-128-gcm / chacha20-ietf-poly1305）")
        };
        _masterKey = NativeCryptoProtocols.EvpBytesToKey(password, _keySize);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_writeSaltSent)
            {
                byte[] salt = RandomNumberGenerator.GetBytes(_saltSize);
                _writeSubkey = HKDF.DeriveKey(HashAlgorithmName.SHA1, _masterKey, _keySize, salt, HkdfInfo);
                _writeCipher = CreateAeadInstance(_writeSubkey);
                await _inner.WriteAsync(salt, cancellationToken);
                _writeSaltSent = true;
            }

            int offset = 0;
            while (offset < buffer.Length)
            {
                int chunkLen = Math.Min(MaxPayloadChunk, buffer.Length - offset);
                int outLen = 2 + TagSize + chunkLen + TagSize;
                byte[] rented = ArrayPool<byte>.Shared.Rent(outLen);
                try
                {
                    EncryptChunkInto(_writeCipher!, buffer.Span.Slice(offset, chunkLen), rented.AsSpan(0, outLen));
                    await _inner.WriteAsync(rented.AsMemory(0, outLen), cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
                offset += chunkLen;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        await _readLock.WaitAsync(cancellationToken);
        try
        {
            if (_readPlainCount > 0)
            {
                int take = Math.Min(buffer.Length, _readPlainCount);
                _readPlainBuffer.AsSpan(_readPlainOffset, take).CopyTo(buffer.Span);
                _readPlainOffset += take;
                _readPlainCount -= take;
                return take;
            }

            if (!_readSaltReceived)
            {
                byte[] salt = new byte[_saltSize];
                if (!await TryReadExactAsync(_inner, salt, cancellationToken))
                    return 0;
                _readSubkey = HKDF.DeriveKey(HashAlgorithmName.SHA1, _masterKey, _keySize, salt, HkdfInfo);
                _readCipher = CreateAeadInstance(_readSubkey);
                _readSaltReceived = true;
            }

            byte[] lenScratch = ArrayPool<byte>.Shared.Rent(2 + TagSize + 2);
            int payloadLen;
            try
            {
                if (!await TryReadExactAsync(_inner, lenScratch.AsMemory(0, 2 + TagSize), cancellationToken))
                    return 0;

                Span<byte> plainLenSpan = lenScratch.AsSpan(2 + TagSize, 2);
                DecryptWithInstance(
                    _readCipher!,
                    _readNonce,
                    lenScratch.AsSpan(0, 2),
                    lenScratch.AsSpan(2, TagSize),
                    plainLenSpan);
                IncrementNonce(_readNonce);

                payloadLen = BinaryPrimitives.ReadUInt16BigEndian(plainLenSpan) & 0x3FFF;
                if (payloadLen == 0) return 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lenScratch);
            }

            byte[] encPayload = ArrayPool<byte>.Shared.Rent(payloadLen + TagSize);
            byte[] plainPayload = ArrayPool<byte>.Shared.Rent(payloadLen);
            try
            {
                if (!await TryReadExactAsync(_inner, encPayload.AsMemory(0, payloadLen + TagSize), cancellationToken))
                    return 0;

                DecryptWithInstance(
                    _readCipher!,
                    _readNonce,
                    encPayload.AsSpan(0, payloadLen),
                    encPayload.AsSpan(payloadLen, TagSize),
                    plainPayload.AsSpan(0, payloadLen));
                IncrementNonce(_readNonce);

                int copy = Math.Min(buffer.Length, payloadLen);
                plainPayload.AsSpan(0, copy).CopyTo(buffer.Span);
                int leftover = payloadLen - copy;
                if (leftover > 0)
                {
                    _readPlainBuffer = plainPayload.AsSpan(copy, leftover).ToArray();
                    _readPlainOffset = 0;
                    _readPlainCount = leftover;
                }
                return copy;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encPayload);
                ArrayPool<byte>.Shared.Return(plainPayload);
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private IDisposable CreateAeadInstance(byte[] subkey) => _cipherKind == CipherKind.ChaCha20Poly1305
        ? new ChaCha20Poly1305(subkey)
        : new AesGcm(subkey, TagSize);

    private void EncryptChunkInto(IDisposable aead, ReadOnlySpan<byte> plaintext, Span<byte> output)
    {
        Span<byte> lenPlain = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenPlain, (ushort)plaintext.Length);

        EncryptWithInstance(aead, _writeNonce, lenPlain, output.Slice(0, 2), output.Slice(2, TagSize));
        IncrementNonce(_writeNonce);

        EncryptWithInstance(aead, _writeNonce, plaintext, output.Slice(2 + TagSize, plaintext.Length), output.Slice(2 + TagSize + plaintext.Length, TagSize));
        IncrementNonce(_writeNonce);
    }

    private static void EncryptWithInstance(IDisposable aead, byte[] nonce, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag)
    {
        if (aead is ChaCha20Poly1305 cc)
            cc.Encrypt(nonce, plain, cipher, tag);
        else
            ((AesGcm)aead).Encrypt(nonce, plain, cipher, tag);
    }

    private static void DecryptWithInstance(IDisposable aead, byte[] nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain)
    {
        if (aead is ChaCha20Poly1305 cc)
            cc.Decrypt(nonce, cipher, tag, plain);
        else
            ((AesGcm)aead).Decrypt(nonce, cipher, tag, plain);
    }

    private static void IncrementNonce(byte[] nonce)
    {
        for (int i = 0; i < nonce.Length; i++)
        {
            if (++nonce[i] != 0) break;
        }
    }

    private static async Task<bool> TryReadExactAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int n = await s.ReadAsync(buf.Slice(offset, buf.Length - offset), ct);
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writeCipher?.Dispose();
            _readCipher?.Dispose();
            _writeLock.Dispose();
            _readLock.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
