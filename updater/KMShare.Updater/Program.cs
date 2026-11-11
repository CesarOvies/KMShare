using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KMShare.Updater;

public enum UpdateVerificationStatus
{
    AcceptedAndVerified = 0,
    RejectedInsecureUrlScheme = 1,
    RejectedHashMismatch = 2,
    RejectedInvalidSignature = 3,
    RejectedMalformedManifest = 4
}

public sealed record UpdateManifest(
    string Version,
    string DownloadUrl,
    string Sha256Hex,
    string SignatureBase64,
    string Algorithm = "Ed25519");

public static class SignedUpdateVerifier
{
    // Official hardcoded Ed25519 32-byte public key (Hex) for production release verification
    public const string OfficialEd25519PublicKeyHex =
        "D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A";

    public static string GetUserSpaceInstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(localAppData, "Programs", "KMShare");
    }

    public static byte[] BuildCanonicalDescriptor(string version, string sha256Hex)
    {
        return Encoding.UTF8.GetBytes($"KMSHARE_UPDATE_V1|{version}|{sha256Hex.ToUpperInvariant()}");
    }

    public static byte[] SignReleaseBinaryEd25519(ReadOnlySpan<byte> privateKeySeed32, string version, ReadOnlySpan<byte> binaryBytes, out string sha256Hex)
    {
        sha256Hex = Convert.ToHexString(SHA256.HashData(binaryBytes));
        byte[] descriptor = BuildCanonicalDescriptor(version, sha256Hex);
        return Ed25519Rfc8032.Sign(privateKeySeed32, descriptor);
    }

    public static byte[] SignReleaseBinaryRsaPss(RSA releasePrivateKey, string version, ReadOnlySpan<byte> binaryBytes, out string sha256Hex)
    {
        sha256Hex = Convert.ToHexString(SHA256.HashData(binaryBytes));
        byte[] descriptor = BuildCanonicalDescriptor(version, sha256Hex);
        return releasePrivateKey.SignData(descriptor, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static UpdateVerificationStatus VerifyPackage(
        UpdateManifest manifest,
        ReadOnlySpan<byte> downloadedBinary,
        ReadOnlySpan<byte> trustedPublicKey)
    {
        if (manifest == null
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
            || string.IsNullOrWhiteSpace(manifest.Sha256Hex)
            || string.IsNullOrWhiteSpace(manifest.SignatureBase64))
        {
            return UpdateVerificationStatus.RejectedMalformedManifest;
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateVerificationStatus.RejectedInsecureUrlScheme;
        }

        byte[] expectedHash;
        byte[] signatureBytes;
        try
        {
            expectedHash = Convert.FromHexString(manifest.Sha256Hex);
            signatureBytes = Convert.FromBase64String(manifest.SignatureBase64);
        }
        catch
        {
            return UpdateVerificationStatus.RejectedMalformedManifest;
        }

        byte[] actualHash = SHA256.HashData(downloadedBinary);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            return UpdateVerificationStatus.RejectedHashMismatch;
        }

        try
        {
            byte[] descriptor = BuildCanonicalDescriptor(manifest.Version, manifest.Sha256Hex);
            bool validSig;

            if (trustedPublicKey.Length == 32 && signatureBytes.Length == 64)
            {
                validSig = Ed25519Rfc8032.Verify(trustedPublicKey, descriptor, signatureBytes);
            }
            else
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(trustedPublicKey, out _);
                validSig = rsa.VerifyData(descriptor, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }

            return validSig
                ? UpdateVerificationStatus.AcceptedAndVerified
                : UpdateVerificationStatus.RejectedInvalidSignature;
        }
        catch
        {
            return UpdateVerificationStatus.RejectedInvalidSignature;
        }
    }

    public static UpdateVerificationStatus VerifyAndStageInstall(
        UpdateManifest manifest,
        ReadOnlySpan<byte> downloadedBinary,
        ReadOnlySpan<byte> trustedPublicKey,
        string targetInstallDir,
        string executableFileName = "KMShare.exe")
    {
        var status = VerifyPackage(manifest, downloadedBinary, trustedPublicKey);
        if (status != UpdateVerificationStatus.AcceptedAndVerified)
        {
            return status;
        }

        Directory.CreateDirectory(targetInstallDir);
        string stagingPath = Path.Combine(targetInstallDir, $"{executableFileName}.staging.tmp");
        string finalPath = Path.Combine(targetInstallDir, executableFileName);

        File.WriteAllBytes(stagingPath, downloadedBinary.ToArray());
        File.Move(stagingPath, finalPath, overwrite: true);
        return UpdateVerificationStatus.AcceptedAndVerified;
    }
}

/// <summary>
/// Pure managed RFC 8032 Ed25519 implementation for zero-dependency portable single-file builds.
/// </summary>
public static class Ed25519Rfc8032
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666, P), P);
    private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y) B = CreateBasePoint();

    private static (BigInteger X, BigInteger Y) CreateBasePoint()
    {
        BigInteger y = Mod(4 * ModInverse(5, P), P);
        BigInteger x = RecoverX(y, 0);
        return (x, y);
    }

    public static byte[] GetPublicKey(ReadOnlySpan<byte> seed32)
    {
        if (seed32.Length != 32) throw new ArgumentException("Ed25519 seed must be 32 bytes.");
        byte[] h = SHA512.HashData(seed32);
        BigInteger s = ClampScalar(h.AsSpan(0, 32));
        var a = ScalarMult(B, s);
        return EncodePoint(a);
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed32, ReadOnlySpan<byte> message)
    {
        if (seed32.Length != 32) throw new ArgumentException("Ed25519 seed must be 32 bytes.");
        byte[] h = SHA512.HashData(seed32);
        BigInteger s = ClampScalar(h.AsSpan(0, 32));
        byte[] pubKey = EncodePoint(ScalarMult(B, s));

        byte[] rInput = new byte[32 + message.Length];
        h.AsSpan(32, 32).CopyTo(rInput);
        message.CopyTo(rInput.AsSpan(32));
        BigInteger r = Mod( new BigInteger(SHA512.HashData(rInput), isUnsigned: true, isBigEndian: false), L);

        byte[] rEncoded = EncodePoint(ScalarMult(B, r));

        byte[] kInput = new byte[64 + message.Length];
        rEncoded.CopyTo(kInput.AsSpan(0, 32));
        pubKey.CopyTo(kInput.AsSpan(32, 32));
        message.CopyTo(kInput.AsSpan(64));
        BigInteger k = Mod(new BigInteger(SHA512.HashData(kInput), isUnsigned: true, isBigEndian: false), L);

        BigInteger S = Mod(r + k * s, L);
        byte[] sBytes = S.ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] sig = new byte[64];
        rEncoded.CopyTo(sig.AsSpan(0, 32));
        sBytes.AsSpan(0, Math.Min(32, sBytes.Length)).CopyTo(sig.AsSpan(32));
        return sig;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature64)
    {
        if (publicKey32.Length != 32 || signature64.Length != 64)
            return false;

        BigInteger S = new BigInteger(signature64.Slice(32, 32), isUnsigned: true, isBigEndian: false);
        if (S >= L) return false;

        if (!TryDecodePoint(publicKey32, out var A) || !TryDecodePoint(signature64[..32], out var R))
            return false;

        byte[] kInput = new byte[64 + message.Length];
        signature64[..32].CopyTo(kInput.AsSpan(0, 32));
        publicKey32.CopyTo(kInput.AsSpan(32, 32));
        message.CopyTo(kInput.AsSpan(64));
        BigInteger k = Mod(new BigInteger(SHA512.HashData(kInput), isUnsigned: true, isBigEndian: false), L);

        var lhs = ScalarMult(B, S);
        var rhs = EdwardsAdd(R, ScalarMult(A, k));
        return lhs.X == rhs.X && lhs.Y == rhs.Y;
    }

    private static BigInteger ClampScalar(ReadOnlySpan<byte> low32)
    {
        Span<byte> a = stackalloc byte[32];
        low32.CopyTo(a);
        a[0] &= 248;
        a[31] &= 127;
        a[31] |= 64;
        return new BigInteger(a, isUnsigned: true, isBigEndian: false);
    }

    private static (BigInteger X, BigInteger Y) EdwardsAdd((BigInteger X, BigInteger Y) p1, (BigInteger X, BigInteger Y) p2)
    {
        BigInteger x1x2 = Mod(p1.X * p2.X, P);
        BigInteger y1y2 = Mod(p1.Y * p2.Y, P);
        BigInteger dxxyy = Mod(D * Mod(x1x2 * y1y2, P), P);

        BigInteger x3 = Mod(Mod(p1.X * p2.Y + p1.Y * p2.X, P) * ModInverse(Mod(1 + dxxyy, P), P), P);
        BigInteger y3 = Mod(Mod(y1y2 + x1x2, P) * ModInverse(Mod(1 - dxxyy, P), P), P);
        return (x3, y3);
    }

    private static (BigInteger X, BigInteger Y) ScalarMult((BigInteger X, BigInteger Y) pt, BigInteger scalar)
    {
        var result = (X: BigInteger.Zero, Y: BigInteger.One);
        var temp = pt;
        BigInteger n = scalar;
        while (n > 0)
        {
            if (!n.IsEven) result = EdwardsAdd(result, temp);
            temp = EdwardsAdd(temp, temp);
            n >>= 1;
        }
        return result;
    }

    private static byte[] EncodePoint((BigInteger X, BigInteger Y) pt)
    {
        byte[] yBytes = pt.Y.ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] res = new byte[32];
        yBytes.AsSpan(0, Math.Min(32, yBytes.Length)).CopyTo(res);
        if (!pt.X.IsEven) res[31] |= 0x80;
        return res;
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> enc, out (BigInteger X, BigInteger Y) pt)
    {
        pt = default;
        if (enc.Length != 32) return false;
        Span<byte> yBuf = stackalloc byte[32];
        enc.CopyTo(yBuf);
        int xSign = (yBuf[31] >> 7) & 1;
        yBuf[31] &= 0x7F;
        BigInteger y = new BigInteger(yBuf, isUnsigned: true, isBigEndian: false);
        if (y >= P) return false;

        try
        {
            BigInteger x = RecoverX(y, xSign);
            pt = (x, y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BigInteger RecoverX(BigInteger y, int sign)
    {
        BigInteger y2 = Mod(y * y, P);
        BigInteger x2 = Mod((y2 - 1) * ModInverse(Mod(D * y2 + 1, P), P), P);
        if (x2.IsZero)
        {
            if (sign != 0) throw new CryptographicException("Invalid point.");
            return BigInteger.Zero;
        }
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2, P) != 0)
            x = Mod(x * I, P);
        if (Mod(x * x - x2, P) != 0)
            throw new CryptographicException("Square root does not exist.");
        if ((x.IsEven ? 0 : 1) != sign)
            x = P - x;
        return x;
    }

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        BigInteger r = a % m;
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m) => BigInteger.ModPow(Mod(a, m), m - 2, m);
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args.Contains("--self-test"))
        {
            return RunSelfTestSuite();
        }

        if (args.Length >= 2 && args[0] == "--check")
        {
            string manifestUrl = args[1];
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                Console.Error.WriteLine("[!] ERRO: O atualizador exige estritamente URLs HTTPS.");
                return 1;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string json = await http.GetStringAsync(uri);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json);
            if (manifest == null)
            {
                Console.Error.WriteLine("[!] Manifesto inválido.");
                return 1;
            }

            byte[] pkgBytes = await http.GetByteArrayAsync(manifest.DownloadUrl);
            byte[] pubKey = Convert.FromHexString(SignedUpdateVerifier.OfficialEd25519PublicKeyHex);
            var status = SignedUpdateVerifier.VerifyAndStageInstall(
                manifest,
                pkgBytes,
                pubKey,
                SignedUpdateVerifier.GetUserSpaceInstallDirectory());

            Console.WriteLine($"[KMShare.Updater] Status de verificação: {status}");
            return status == UpdateVerificationStatus.AcceptedAndVerified ? 0 : 2;
        }

        Console.WriteLine("Uso: kmshare-updater [--self-test | --check <https://.../manifest.json>]");
        return 0;
    }

    public static int RunSelfTestSuite()
    {
        Console.WriteLine("=== KMShare.Updater Cryptographic Verification Self-Test (Ed25519 & RSA-PSS) ===");

        // Generate official Ed25519 seed (RFC 8032 Test Vector 1 seed -> matches OfficialEd25519PublicKeyHex)
        byte[] officialSeed = Convert.FromHexString("9D61B19DEFFD5A60BA844AF492EC2CC44449C5697B326919703BAC031CAE7F60");
        byte[] officialPub = Ed25519Rfc8032.GetPublicKey(officialSeed);
        Trace.Assert(Convert.ToHexString(officialPub) == SignedUpdateVerifier.OfficialEd25519PublicKeyHex);

        byte[] attackerSeed = RandomNumberGenerator.GetBytes(32);

        byte[] legitimateBinary = "MZ_KMSHARE_SIGNED_PORTABLE_BINARY_V1.2.0"u8.ToArray();
        byte[] validEd25519Sig = SignedUpdateVerifier.SignReleaseBinaryEd25519(officialSeed, "1.2.0", legitimateBinary, out string validSha256);

        var validManifest = new UpdateManifest(
            Version: "1.2.0",
            DownloadUrl: "https://updates.kmshare.internal/releases/KMShare-1.2.0.exe",
            Sha256Hex: validSha256,
            SignatureBase64: Convert.ToBase64String(validEd25519Sig),
            Algorithm: "Ed25519");

        // 1. Reject non-HTTPS downgrade
        var httpManifest = validManifest with { DownloadUrl = "http://updates.kmshare.internal/releases/KMShare-1.2.0.exe" };
        var resHttp = SignedUpdateVerifier.VerifyPackage(httpManifest, legitimateBinary, officialPub);
        Console.WriteLine($"  -> HTTP (non-TLS) manifest check: {resHttp}");
        Trace.Assert(resHttp == UpdateVerificationStatus.RejectedInsecureUrlScheme);

        // 2. Reject tampered binary (hash mismatch)
        byte[] tamperedBinary = (byte[])legitimateBinary.Clone();
        tamperedBinary[^1] ^= 0xFF;
        var resTampered = SignedUpdateVerifier.VerifyPackage(validManifest, tamperedBinary, officialPub);
        Console.WriteLine($"  -> Tampered binary payload check: {resTampered}");
        Trace.Assert(resTampered == UpdateVerificationStatus.RejectedHashMismatch);

        // 3. Reject binary signed by untrusted/attacker Ed25519 private key
        byte[] attackerSig = SignedUpdateVerifier.SignReleaseBinaryEd25519(attackerSeed, "1.2.0", tamperedBinary, out string attackerHash);
        var attackerManifest = new UpdateManifest(
            Version: "1.2.0",
            DownloadUrl: "https://updates.kmshare.internal/releases/KMShare-1.2.0.exe",
            Sha256Hex: attackerHash,
            SignatureBase64: Convert.ToBase64String(attackerSig),
            Algorithm: "Ed25519");
        var resAttacker = SignedUpdateVerifier.VerifyPackage(attackerManifest, tamperedBinary, officialPub);
        Console.WriteLine($"  -> Forged Ed25519 signature (wrong private key) check: {resAttacker}");
        Trace.Assert(resAttacker == UpdateVerificationStatus.RejectedInvalidSignature);

        // 4. Accept & stage valid Ed25519 signed binary into isolated user-space directory
        string tempInstallDir = Path.Combine(Path.GetTempPath(), $"KMShare_UpdaterTest_{Guid.NewGuid():N}");
        try
        {
            var resValid = SignedUpdateVerifier.VerifyAndStageInstall(
                validManifest,
                legitimateBinary,
                officialPub,
                tempInstallDir,
                "KMShare.exe");
            string installedExe = Path.Combine(tempInstallDir, "KMShare.exe");
            Console.WriteLine($"  -> Valid official Ed25519 signature & user-space staging: {resValid} (FileExists={File.Exists(installedExe)})");
            Trace.Assert(resValid == UpdateVerificationStatus.AcceptedAndVerified && File.Exists(installedExe));
        }
        finally
        {
            if (Directory.Exists(tempInstallDir))
            {
                try { Directory.Delete(tempInstallDir, recursive: true); } catch { }
            }
        }

        Console.WriteLine("  [OK] KMShare.Updater Self-Test Passed.\n");
        return 0;
    }
}
