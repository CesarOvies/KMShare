using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MKShare.Security;

public sealed class FileTransferGatekeeper
{
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, long> _activeTokensWithExpiryMs = new();
    private const long TokenLifetimeMs = 60_000; // 60 seconds validity per transaction

    public byte[] AuthorizeTransaction(string fileName, ReadOnlySpan<byte> fileContent, bool requireInteractiveOsPrompt)
    {
        byte[] fileHash = SHA256.HashData(fileContent);
        return AuthorizeTransactionByOffer(fileName, fileContent.Length, fileHash, requireInteractiveOsPrompt);
    }

    public byte[] AuthorizeTransactionByOffer(
        string fileName,
        long fileSize,
        ReadOnlySpan<byte> expectedSha256,
        bool requireInteractiveOsPrompt)
    {
        string safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || expectedSha256.Length != 32 || fileSize < 0)
            return [];

        bool userApproved = !requireInteractiveOsPrompt || OsStepUpAuth.PromptUserConsent(
            $"Autorizar recebimento do arquivo '{safeName}' ({fileSize} bytes)?");

        if (!userApproved) return [];

        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        byte[] mac = ComputeTransactionMac(nonce, safeName, fileSize, expectedSha256);
        byte[] token = [.. nonce, .. mac];

        _activeTokensWithExpiryMs[Convert.ToHexString(token)] = Environment.TickCount64 + TokenLifetimeMs;
        return token;
    }

    public bool TryCommitTransfer(string fileName, ReadOnlySpan<byte> fileContent, ReadOnlySpan<byte> token)
    {
        if (token.Length != 48) return false;

        string safeName = Path.GetFileName(fileName);
        string tokenHex = Convert.ToHexString(token);
        // Immediate consumption (Fail-Closed: burns token on first attempt, even if payload is tampered)
        if (!_activeTokensWithExpiryMs.Remove(tokenHex, out long expiresAtMs))
            return false;

        if (Environment.TickCount64 > expiresAtMs)
            return false;

        ReadOnlySpan<byte> nonce = token[..16];
        ReadOnlySpan<byte> providedMac = token.Slice(16, 32);
        byte[] actualHash = SHA256.HashData(fileContent);
        byte[] expectedMac = ComputeTransactionMac(nonce, safeName, fileContent.Length, actualHash);

        return CryptographicOperations.FixedTimeEquals(providedMac, expectedMac);
    }

    public bool IsTokenActive(ReadOnlySpan<byte> token)
    {
        if (token.Length != 48) return false;
        return _activeTokensWithExpiryMs.ContainsKey(Convert.ToHexString(token));
    }

    private byte[] ComputeTransactionMac(ReadOnlySpan<byte> nonce, string fileName, long fileLength, ReadOnlySpan<byte> fileHash)
    {
        string descriptor = $"{Convert.ToHexString(nonce)}|{fileName}|{fileLength}|{Convert.ToHexString(fileHash)}";
        return HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(descriptor));
    }
}

public static class OsStepUpAuth
{
    public static bool IsNativeAuthAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (NativeLibrary.TryLoad("credui.dll", out IntPtr lib))
            {
                bool ok = NativeLibrary.TryGetExport(lib, "CredUIPromptForWindowsCredentialsW", out _);
                NativeLibrary.Free(lib);
                return ok;
            }
            return false;
        }
        return true;
    }

    public static bool PromptUserConsent(string promptMessage)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return PromptWindowsCredUi(promptMessage);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RunProcessAuth("/usr/bin/osascript",
                $"-e \"display dialog \\\"{promptMessage}\\\" with title \\\"MKShare Auth\\\" buttons {{\\\"Cancel\\\", \\\"Allow\\\"}} default button \\\"Allow\\\"\"");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RunProcessAuth("pkexec", "--version");
        }

        return false;
    }

    private static bool RunProcessAuth(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(30_000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentialsW(
        ref CREDUI_INFO pUiInfo,
        int dwAuthError,
        ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer,
        uint ulInAuthBufferSize,
        out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize,
        ref bool pfSave,
        uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static bool PromptWindowsCredUi(string message)
    {
        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            pszCaptionText = "MKShare - Autorização por Transação",
            pszMessageText = message
        };
        uint authPackage = 0;
        bool save = false;
        const uint CREDUIWIN_GENERIC = 0x1;

        int status = CredUIPromptForWindowsCredentialsW(
            ref info, 0, ref authPackage, IntPtr.Zero, 0,
            out IntPtr outBuf, out _, ref save, CREDUIWIN_GENERIC);

        if (outBuf != IntPtr.Zero)
        {
            CoTaskMemFree(outBuf);
        }
        return status == 0;
    }
}
