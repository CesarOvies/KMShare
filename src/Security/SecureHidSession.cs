using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MKShare.Security;

public enum PacketStatus
{
    Accepted,
    RejectedUnauthenticated,
    RevokedCryptoTamperOrReplay,
    RevokedNonHidPayload,
    RevokedSuperhumanRateAnomaly,
    RevokedSuspiciousShellShortcut
}

public readonly record struct HidEvent(byte Type, short Arg1, short Arg2);

public static class HidFrame
{
    // Strict 6-byte wire format:
    // [0]: Magic 0x4D ('M')
    // [1]: Type (0x01=MouseMove, 0x02=MouseButton, 0x03=KeyEvent)
    // [2..3]: Int16 (dx / buttonId / scancode)
    // [4..5]: Int16 (dy / buttonState / keyState)
    public const int Size = 6;
    public const byte Magic = 0x4D;
    public const byte MouseMove = 0x01;
    public const byte MouseButton = 0x02;
    public const byte KeyEvent = 0x03;

    public static byte[] EncodeMouseMove(short dx, short dy) => Encode(MouseMove, dx, dy);
    public static byte[] EncodeMouseButton(short button, bool isDown) => Encode(MouseButton, button, (short)(isDown ? 1 : 0));
    public static byte[] EncodeKey(ushort scancode, bool isDown) => Encode(KeyEvent, (short)scancode, (short)(isDown ? 1 : 0));

    private static byte[] Encode(byte type, short a1, short a2)
    {
        byte[] buf = new byte[Size];
        buf[0] = Magic;
        buf[1] = type;
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(2, 2), a1);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(4, 2), a2);
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> buf, out HidEvent ev)
    {
        ev = default;
        if (buf.Length != Size || buf[0] != Magic) return false;

        byte type = buf[1];
        if (type is not (MouseMove or MouseButton or KeyEvent)) return false;

        short a1 = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(2, 2));
        short a2 = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(4, 2));

        if (type is MouseButton or KeyEvent && a2 is not (0 or 1))
            return false;

        ev = new HidEvent(type, a1, a2);
        return true;
    }
}

public sealed class SecureHidSession(int maxDistinctKeystrokesPerSecond = 30) : IDisposable
{
    private byte[]? _sessionKey;
    private ulong _txSeq;
    private ulong _rxLastSeq;
    private readonly Queue<long> _keystrokeWindow = new();
    private short _lastKeyDownScancode = -1;
    private bool _modifierSuperHeld;
    private string? _peerId;
    private TrustedPeerVault? _vault;

    // Scan codes for Windows/Super keys & 'R'
    private const short ScanLeftWin = 0x5B;
    private const short ScanRightWin = 0x5C;
    private const short ScanKeyR = 0x13;

    public bool IsAuthenticated => _sessionKey != null;
    public string? PeerId => _peerId;

    public static (ECDiffieHellman Ecdh, byte[] PublicSpki) CreateHandshakeKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (ecdh, ecdh.ExportSubjectPublicKeyInfo());
    }

    public void EstablishSession(
        ECDiffieHellman ownEcdh,
        ReadOnlySpan<byte> peerSpki,
        string pairingPin,
        string? peerId = null,
        TrustedPeerVault? vault = null)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out _);

        byte[] sharedSecret = ownEcdh.DeriveRawSecretAgreement(peer.PublicKey);
        byte[] pinBytes = Encoding.UTF8.GetBytes(pairingPin);

        _sessionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            outputLength: 32,
            salt: pinBytes,
            info: "MKSHARE_HID_V1"u8.ToArray());

        CryptographicOperations.ZeroMemory(sharedSecret);
        _txSeq = 0;
        _rxLastSeq = 0;
        _keystrokeWindow.Clear();
        _lastKeyDownScancode = -1;
        _modifierSuperHeld = false;

        _peerId = peerId;
        _vault = vault;
        if (!string.IsNullOrWhiteSpace(_peerId))
        {
            (_vault ?? TrustedPeerVault.Default).SaveTrustedPeer(_peerId, peerSpki);
        }
    }

    public byte[] SealFrame(ReadOnlySpan<byte> hidFrame)
    {
        if (_sessionKey == null)
            throw new InvalidOperationException("Session is not authenticated.");

        ulong seq = ++_txSeq;
        byte[] packet = new byte[8 + hidFrame.Length + 16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(0, 8), seq);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), seq);

        using var aes = new AesGcm(_sessionKey, 16);
        aes.Encrypt(
            nonce,
            hidFrame,
            packet.AsSpan(8, hidFrame.Length),
            packet.AsSpan(8 + hidFrame.Length, 16),
            associatedData: packet.AsSpan(0, 8));

        return packet;
    }

    public PacketStatus OpenAndValidatePacket(ReadOnlySpan<byte> packet, out HidEvent hidEvent)
    {
        hidEvent = default;
        if (_sessionKey == null)
            return PacketStatus.RejectedUnauthenticated;

        if (packet.Length < 8 + 16)
        {
            RevokeAuthentication();
            return PacketStatus.RevokedNonHidPayload;
        }

        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(packet[..8]);
        if (seq <= _rxLastSeq)
        {
            RevokeAuthentication();
            return PacketStatus.RevokedCryptoTamperOrReplay;
        }

        int cipherLen = packet.Length - 8 - 16;
        Span<byte> plaintext = cipherLen <= 256 ? stackalloc byte[cipherLen] : new byte[cipherLen];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), seq);

        try
        {
            using var aes = new AesGcm(_sessionKey, 16);
            aes.Decrypt(
                nonce,
                packet.Slice(8, cipherLen),
                packet.Slice(8 + cipherLen, 16),
                plaintext,
                associatedData: packet[..8]);
        }
        catch (CryptographicException)
        {
            RevokeAuthentication();
            return PacketStatus.RevokedCryptoTamperOrReplay;
        }

        _rxLastSeq = seq;

        if (!HidFrame.TryDecode(plaintext, out hidEvent))
        {
            RevokeAuthentication();
            return PacketStatus.RevokedNonHidPayload;
        }

        if (hidEvent.Type == HidFrame.KeyEvent)
        {
            short scancode = hidEvent.Arg1;
            bool isDown = hidEvent.Arg2 == 1;

            if (scancode is ScanLeftWin or ScanRightWin)
            {
                _modifierSuperHeld = isDown;
            }
            else if (isDown && _modifierSuperHeld && scancode == ScanKeyR)
            {
                RevokeAuthentication();
                return PacketStatus.RevokedSuspiciousShellShortcut;
            }

            if (!isDown)
            {
                if (_lastKeyDownScancode == scancode)
                    _lastKeyDownScancode = -1;
            }
            else if (!IsModifierKey(scancode) && scancode != _lastKeyDownScancode)
            {
                // Only count distinct non-modifier key-down transitions (ignores OS typematic key-hold repeats)
                _lastKeyDownScancode = scancode;

                long now = Environment.TickCount64;
                _keystrokeWindow.Enqueue(now);
                while (_keystrokeWindow.Count > 0 && now - _keystrokeWindow.Peek() > 1000)
                {
                    _keystrokeWindow.Dequeue();
                }

                if (_keystrokeWindow.Count > maxDistinctKeystrokesPerSecond)
                {
                    RevokeAuthentication();
                    return PacketStatus.RevokedSuperhumanRateAnomaly;
                }
            }
        }

        return PacketStatus.Accepted;
    }

    private static bool IsModifierKey(short scancode)
    {
        // Left/Right Win (0x5B, 0x5C), Ctrl (0x1D), LShift (0x2A), RShift (0x36), Alt (0x38)
        return scancode is ScanLeftWin or ScanRightWin or 0x1D or 0x2A or 0x36 or 0x38;
    }

    public void RevokeAuthentication()
    {
        if (_sessionKey != null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }
        if (!string.IsNullOrWhiteSpace(_peerId))
        {
            (_vault ?? TrustedPeerVault.Default).RevokeAndDeletePeer(_peerId);
        }
        _txSeq = 0;
        _rxLastSeq = 0;
        _keystrokeWindow.Clear();
        _lastKeyDownScancode = -1;
        _modifierSuperHeld = false;
    }

    public void Dispose()
    {
        if (_sessionKey != null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }
    }
}

/// <summary>
/// Persists trusted peer public keys in the user-space OS vault (Windows DPAPI CurrentUser or Unix 0600 keyring)
/// and immediately deletes them upon security revocation.
/// </summary>
public sealed class TrustedPeerVault
{
    public static TrustedPeerVault Default { get; } = new();

    private readonly string _vaultFilePath;
    private readonly object _sync = new();

    public TrustedPeerVault(string? customVaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customVaultPath))
        {
            _vaultFilePath = customVaultPath;
        }
        else
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            _vaultFilePath = Path.Combine(baseDir, "KMShare", "trusted_peers.vault");
        }
    }

    public string VaultFilePath => _vaultFilePath;

    public void SaveTrustedPeer(string peerId, ReadOnlySpan<byte> peerPublicSpki)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        lock (_sync)
        {
            var map = LoadMapUnsafe();
            map[peerId] = Convert.ToBase64String(peerPublicSpki);
            SaveMapUnsafe(map);
        }
    }

    public bool TryGetTrustedPeerKey(string peerId, out byte[] peerPublicSpki)
    {
        peerPublicSpki = [];
        if (string.IsNullOrWhiteSpace(peerId)) return false;

        lock (_sync)
        {
            var map = LoadMapUnsafe();
            if (map.TryGetValue(peerId, out string? b64) && !string.IsNullOrEmpty(b64))
            {
                peerPublicSpki = Convert.FromBase64String(b64);
                return true;
            }
            return false;
        }
    }

    public bool IsPeerTrusted(string peerId, ReadOnlySpan<byte> peerPublicSpki)
    {
        if (!TryGetTrustedPeerKey(peerId, out byte[] stored))
            return false;
        return CryptographicOperations.FixedTimeEquals(stored, peerPublicSpki);
    }

    public bool RevokeAndDeletePeer(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return false;
        lock (_sync)
        {
            var map = LoadMapUnsafe();
            bool removed = map.Remove(peerId);
            if (removed)
            {
                SaveMapUnsafe(map);
            }
            return removed;
        }
    }

    private Dictionary<string, string> LoadMapUnsafe()
    {
        try
        {
            if (!File.Exists(_vaultFilePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            byte[] encrypted = File.ReadAllBytes(_vaultFilePath);
            byte[] plaintext = UnprotectUserBytes(encrypted);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext);
            return loaded != null
                ? new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveMapUnsafe(Dictionary<string, string> map)
    {
        string? dir = Path.GetDirectoryName(_vaultFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(map);
        byte[] encrypted = ProtectUserBytes(plaintext);
        File.WriteAllBytes(_vaultFilePath, encrypted);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(_vaultFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
            }
        }
    }

    private static byte[] ProtectUserBytes(byte[] plaintext)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DpapiProtect(plaintext);
        }

        byte[] key = DeriveUnixKeyringKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] UnprotectUserBytes(byte[] protectedBytes)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DpapiUnprotect(protectedBytes);
        }

        if (protectedBytes.Length < 28)
            throw new CryptographicException("Invalid keyring blob.");

        byte[] key = DeriveUnixKeyringKey();
        byte[] plain = new byte[protectedBytes.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(
            protectedBytes.AsSpan(0, 12),
            protectedBytes.AsSpan(28),
            protectedBytes.AsSpan(12, 16),
            plain);
        CryptographicOperations.ZeroMemory(key);
        return plain;
    }

    private static byte[] DeriveUnixKeyringKey()
    {
        byte[] ikm = Encoding.UTF8.GetBytes($"{Environment.UserName}@{Environment.MachineName}:KMSHARE_KEYRING_V1");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, info: "KMSHARE_VAULT"u8.ToArray());
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiProtect(byte[] plaintext)
    {
        IntPtr pIn = Marshal.AllocHGlobal(plaintext.Length);
        try
        {
            Marshal.Copy(plaintext, 0, pIn, plaintext.Length);
            var blobIn = new DATA_BLOB { cbData = plaintext.Length, pbData = pIn };
            if (!CryptProtectData(ref blobIn, "KMShareTrustedPeers", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB blobOut))
            {
                throw new CryptographicException($"CryptProtectData failed (err={Marshal.GetLastWin32Error()})");
            }

            try
            {
                byte[] result = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                return result;
            }
            finally
            {
                if (blobOut.pbData != IntPtr.Zero) LocalFree(blobOut.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
        }
    }

    private static byte[] DpapiUnprotect(byte[] encrypted)
    {
        IntPtr pIn = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, pIn, encrypted.Length);
            var blobIn = new DATA_BLOB { cbData = encrypted.Length, pbData = pIn };
            if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB blobOut))
            {
                throw new CryptographicException($"CryptUnprotectData failed (err={Marshal.GetLastWin32Error()})");
            }

            try
            {
                byte[] result = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                return result;
            }
            finally
            {
                if (blobOut.pbData != IntPtr.Zero) LocalFree(blobOut.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
        }
    }
}
