using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MKShare.Security;

namespace MKShare.FileTransfer;

public enum FileTransferStatus
{
    Committed = 0,
    RejectedStepUpDenied = 1,
    RejectedTamperedOrInvalidToken = 2,
    RejectedProtocolOrCryptoError = 3
}

public sealed record FileOfferPayload(string FileName, long FileSize, string Sha256Hex);

public sealed record FileTransferResult(
    FileTransferStatus Status,
    string FileName,
    long BytesTransferred,
    string? SavedFilePath,
    byte[] IssuedToken);

public sealed class SecureFileChannel
{
    public const int DefaultTcpFilePort = 51821;
    public const int ChunkSize = 16 * 1024;

    private const byte FrameHello = 0x10;
    private const byte FrameHelloAck = 0x11;
    private const byte FrameOffer = 0x20;
    private const byte FrameTokenIssued = 0x21;
    private const byte FrameTokenDenied = 0x22;
    private const byte FrameChunk = 0x30;
    private const byte FrameCommit = 0x31;
    private const byte FrameResult = 0x40;

    public static string GetDefaultDownloadsDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(userProfile, "Downloads");
        return Directory.Exists(downloads) ? downloads : Path.GetTempPath();
    }

    public static async Task<FileTransferResult> ReceiveSingleFileOverTcpAsync(
        TcpListener listener,
        string pairingPin,
        FileTransferGatekeeper gatekeeper,
        string destinationDirectory,
        bool requireInteractiveOsPrompt,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(ct);
        if (client.Client.RemoteEndPoint is IPEndPoint remoteEp)
        {
            IntranetGuard.EnforceLanEndpoint(remoteEp);
        }

        await using NetworkStream netStream = client.GetStream();
        return await ReceiveFileOverStreamAsync(
            netStream,
            pairingPin,
            gatekeeper,
            destinationDirectory,
            requireInteractiveOsPrompt,
            onProgress,
            ct);
    }

    public static async Task<FileTransferResult> SendSingleFileOverTcpAsync(
        IPAddress localBindIp,
        IPEndPoint targetEndpoint,
        string pairingPin,
        string sourceFilePath,
        Action<long, long>? onProgress = null,
        byte[]? overrideTokenForTest = null,
        bool tamperPayloadInTransitForTest = false,
        CancellationToken ct = default)
    {
        IntranetGuard.EnforceLanEndpoint(new IPEndPoint(localBindIp, 0));
        IntranetGuard.EnforceLanEndpoint(targetEndpoint);

        using var client = new TcpClient(new IPEndPoint(localBindIp, 0));
        await client.ConnectAsync(targetEndpoint, ct);
        await using NetworkStream netStream = client.GetStream();

        byte[] content = await File.ReadAllBytesAsync(sourceFilePath, ct);
        string fileName = Path.GetFileName(sourceFilePath);

        return await SendBytesOverStreamAsync(
            netStream,
            pairingPin,
            fileName,
            content,
            onProgress,
            overrideTokenForTest,
            tamperPayloadInTransitForTest,
            ct);
    }

    public static async Task<FileTransferResult> ReceiveFileOverStreamAsync(
        Stream stream,
        string pairingPin,
        FileTransferGatekeeper gatekeeper,
        string destinationDirectory,
        bool requireInteractiveOsPrompt,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        byte[]? hello = await ReadRawFrameAsync(stream, ct);
        if (hello == null || hello.Length < 2 || hello[0] != FrameHello)
            return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, "", 0, null, []);

        byte[] sessionKey;
        var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
        using (ownEcdh)
        {
            sessionKey = DeriveFileChannelKey(ownEcdh, hello.AsSpan(1), pairingPin);
            await WriteRawFrameAsync(stream, [FrameHelloAck, .. ownPub], ct);
        }

        ulong txSeq = 0;
        ulong rxSeq = 0;

        try
        {
            // Stage 1: Receive Encrypted FileOffer
            byte[]? offerPlain = await ReadEncryptedFrameAsync(stream, sessionKey, FrameOffer, () => ++rxSeq, ct);
            if (offerPlain == null)
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, "", 0, null, []);

            var offer = JsonSerializer.Deserialize<FileOfferPayload>(offerPlain);
            if (offer == null || string.IsNullOrWhiteSpace(offer.FileName) || offer.FileSize < 0)
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, "", 0, null, []);

            string safeFileName = Path.GetFileName(offer.FileName);
            byte[] expectedSha256 = Convert.FromHexString(offer.Sha256Hex);

            // Stage 2: Step-Up OS Consent Prompt + One-Time Token Issuance
            byte[] issuedToken = gatekeeper.AuthorizeTransactionByOffer(
                safeFileName,
                offer.FileSize,
                expectedSha256,
                requireInteractiveOsPrompt);

            if (issuedToken.Length != 48)
            {
                await WriteEncryptedFrameAsync(stream, sessionKey, FrameTokenDenied, [], () => ++txSeq, ct);
                return new FileTransferResult(FileTransferStatus.RejectedStepUpDenied, safeFileName, 0, null, []);
            }

            await WriteEncryptedFrameAsync(stream, sessionKey, FrameTokenIssued, issuedToken, () => ++txSeq, ct);

            // Stage 3: Receive Encrypted ChunkStream into temporary .part file & Commit with Token Verification
            string partPath = Path.Combine(destinationDirectory, $"{safeFileName}.{Guid.NewGuid():N}.part");
            string finalPath = Path.Combine(destinationDirectory, safeFileName);
            byte[]? commitToken = null;
            long receivedBytes = 0;

            try
            {
                await using (var partFs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        var (frameType, plain) = await ReadAnyEncryptedFrameAsync(stream, sessionKey, () => ++rxSeq, ct);
                        if (frameType == FrameChunk)
                        {
                            await partFs.WriteAsync(plain, ct);
                            receivedBytes += plain.Length;
                            onProgress?.Invoke(receivedBytes, offer.FileSize);
                        }
                        else if (frameType == FrameCommit)
                        {
                            commitToken = plain;
                            break;
                        }
                        else
                        {
                            throw new InvalidDataException("Unexpected frame during chunk stream.");
                        }
                    }
                    await partFs.FlushAsync(ct);
                }

                byte[] actualFileBytes = await File.ReadAllBytesAsync(partPath, ct);
                bool validCommit = commitToken != null && gatekeeper.TryCommitTransfer(safeFileName, actualFileBytes, commitToken);

                if (!validCommit)
                {
                    if (File.Exists(partPath)) File.Delete(partPath);
                    await WriteEncryptedFrameAsync(stream, sessionKey, FrameResult, [(byte)FileTransferStatus.RejectedTamperedOrInvalidToken], () => ++txSeq, ct);
                    return new FileTransferResult(FileTransferStatus.RejectedTamperedOrInvalidToken, safeFileName, receivedBytes, null, issuedToken);
                }

                File.Move(partPath, finalPath, overwrite: true);
                await WriteEncryptedFrameAsync(stream, sessionKey, FrameResult, [(byte)FileTransferStatus.Committed], () => ++txSeq, ct);
                return new FileTransferResult(FileTransferStatus.Committed, safeFileName, receivedBytes, finalPath, issuedToken);
            }
            catch
            {
                if (File.Exists(partPath))
                {
                    try { File.Delete(partPath); } catch { }
                }
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, safeFileName, receivedBytes, null, issuedToken);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    public static async Task<FileTransferResult> SendBytesOverStreamAsync(
        Stream stream,
        string pairingPin,
        string fileName,
        byte[] fileContent,
        Action<long, long>? onProgress = null,
        byte[]? overrideTokenForTest = null,
        bool tamperPayloadInTransitForTest = false,
        CancellationToken ct = default)
    {
        string safeFileName = Path.GetFileName(fileName);
        byte[] sessionKey;
        var (ownEcdh, ownPub) = SecureHidSession.CreateHandshakeKeyPair();
        using (ownEcdh)
        {
            await WriteRawFrameAsync(stream, [FrameHello, .. ownPub], ct);
            byte[]? ack = await ReadRawFrameAsync(stream, ct);
            if (ack == null || ack.Length < 2 || ack[0] != FrameHelloAck)
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, safeFileName, 0, null, []);

            sessionKey = DeriveFileChannelKey(ownEcdh, ack.AsSpan(1), pairingPin);
        }

        ulong txSeq = 0;
        ulong rxSeq = 0;

        try
        {
            // Stage 1: Send FileOffer with true SHA-256 of original file
            string sha256Hex = Convert.ToHexString(SHA256.HashData(fileContent));
            var offer = new FileOfferPayload(safeFileName, fileContent.Length, sha256Hex);
            byte[] offerBytes = JsonSerializer.SerializeToUtf8Bytes(offer);
            await WriteEncryptedFrameAsync(stream, sessionKey, FrameOffer, offerBytes, () => ++txSeq, ct);

            // Stage 2: Await Step-Up Auth Token from Receiver
            var (respType, tokenPayload) = await ReadAnyEncryptedFrameAsync(stream, sessionKey, () => ++rxSeq, ct);
            if (respType == FrameTokenDenied)
            {
                return new FileTransferResult(FileTransferStatus.RejectedStepUpDenied, safeFileName, 0, null, []);
            }
            if (respType != FrameTokenIssued || tokenPayload.Length != 48)
            {
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, safeFileName, 0, null, []);
            }

            // Stage 3: Stream Encrypted Chunks & Commit with One-Time Token
            byte[] payloadToSend = (byte[])fileContent.Clone();
            if (tamperPayloadInTransitForTest && payloadToSend.Length > 0)
            {
                payloadToSend[0] ^= 0xFF;
            }

            long offset = 0;
            while (offset < payloadToSend.Length)
            {
                int count = (int)Math.Min(ChunkSize, payloadToSend.Length - offset);
                await WriteEncryptedFrameAsync(
                    stream,
                    sessionKey,
                    FrameChunk,
                    payloadToSend.AsSpan((int)offset, count).ToArray(),
                    () => ++txSeq,
                    ct);
                offset += count;
                onProgress?.Invoke(offset, payloadToSend.Length);
            }

            byte[] commitToken = overrideTokenForTest ?? tokenPayload;
            await WriteEncryptedFrameAsync(stream, sessionKey, FrameCommit, commitToken, () => ++txSeq, ct);

            byte[]? resultBytes = await ReadEncryptedFrameAsync(stream, sessionKey, FrameResult, () => ++rxSeq, ct);
            if (resultBytes == null || resultBytes.Length < 1)
                return new FileTransferResult(FileTransferStatus.RejectedProtocolOrCryptoError, safeFileName, offset, null, tokenPayload);

            var status = (FileTransferStatus)resultBytes[0];
            return new FileTransferResult(status, safeFileName, offset, null, tokenPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private static byte[] DeriveFileChannelKey(ECDiffieHellman ownEcdh, ReadOnlySpan<byte> peerSpki, string pin)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out _);
        byte[] sharedSecret = ownEcdh.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                outputLength: 32,
                salt: Encoding.UTF8.GetBytes(pin),
                info: "MKSHARE_FILE_V1"u8.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static async Task WriteEncryptedFrameAsync(
        Stream stream,
        byte[] key,
        byte frameType,
        byte[] plaintext,
        Func<ulong> nextSeq,
        CancellationToken ct)
    {
        ulong seq = nextSeq();
        byte[] envelope = new byte[1 + 8 + plaintext.Length + 16];
        envelope[0] = frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(1, 8), seq);

        Span<byte> nonce = stackalloc byte[12];
        nonce[0] = frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), seq);

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(
            nonce,
            plaintext,
            envelope.AsSpan(9, plaintext.Length),
            envelope.AsSpan(9 + plaintext.Length, 16),
            associatedData: envelope.AsSpan(0, 9));

        await WriteRawFrameAsync(stream, envelope, ct);
    }

    private static async Task<byte[]?> ReadEncryptedFrameAsync(
        Stream stream,
        byte[] key,
        byte expectedType,
        Func<ulong> nextExpectedSeq,
        CancellationToken ct)
    {
        var (actualType, plain) = await ReadAnyEncryptedFrameAsync(stream, key, nextExpectedSeq, ct);
        return actualType == expectedType ? plain : null;
    }

    private static async Task<(byte FrameType, byte[] Plaintext)> ReadAnyEncryptedFrameAsync(
        Stream stream,
        byte[] key,
        Func<ulong> nextExpectedSeq,
        CancellationToken ct)
    {
        byte[]? raw = await ReadRawFrameAsync(stream, ct);
        if (raw == null || raw.Length < 1 + 8 + 16)
            return (0, []);

        byte frameType = raw[0];
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(1, 8));
        ulong expected = nextExpectedSeq();
        if (seq != expected)
            return (0, []);

        int cipherLen = raw.Length - 9 - 16;
        byte[] plain = new byte[cipherLen];
        Span<byte> nonce = stackalloc byte[12];
        nonce[0] = frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), seq);

        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                nonce,
                raw.AsSpan(9, cipherLen),
                raw.AsSpan(9 + cipherLen, 16),
                plain,
                associatedData: raw.AsSpan(0, 9));
            return (frameType, plain);
        }
        catch (CryptographicException)
        {
            return (0, []);
        }
    }

    private static async Task WriteRawFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task<byte[]?> ReadRawFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        int r = await stream.ReadAtLeastAsync(prefix, 4, throwOnEndOfStream: false, ct);
        if (r < 4) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (len < 0 || len > 64 * 1024 * 1024) return null;
        byte[] buf = new byte[len];
        int r2 = await stream.ReadAtLeastAsync(buf, len, throwOnEndOfStream: false, ct);
        return r2 < len ? null : buf;
    }
}

