using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace KMShare.Security;

/// <summary>
/// Gerencia a auto-verificação da assinatura de código do KMShare e disponibilização da chave pública (.cer)
/// para instalação manual opcional no escopo do Usuário, sem forçar injeção no repositório de certificados do Windows.
/// </summary>
public static class WindowsAuthenticodeTrust
{
    public const string PublicCertFileName = "KMShare-CodeSigning.cer";

    /// <summary>
    /// Verifica se o binário está assinado, disponibiliza a chave pública (.cer) ao lado do executável
    /// para instalação manual opcional e verifica se o certificado já é confiado no escopo do usuário (somente leitura).
    /// Não realiza injeção forçada de certificados no repositório do Windows para evitar alertas de segurança (Sigma / EDR).
    /// </summary>
    public static bool EnsureSelfTrust(Action<string>? logAction = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        X509Certificate2? cert = null;
#pragma warning disable SYSLIB0057
        try
        {
            using var rawCert = X509Certificate.CreateFromSignedFile(exePath);
            cert = new X509Certificate2(rawCert);
        }
        catch
        {
            // Binário não assinado (ex: build de depuração/desenvolvimento local)
            return false;
        }
#pragma warning restore SYSLIB0057

        if (cert == null)
        {
            return false;
        }

        using (cert)
        {
            // 1. Sempre disponibiliza a cópia da chave pública (.cer) ao lado do executável para instalação manual opcional
            TryEnsurePublicCertFile(exePath, cert, logAction);

            // 2. Apenas verifica se o certificado já é confiado na raiz do usuário atual (somente leitura)
            try
            {
                using var readRootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                readRootStore.Open(OpenFlags.ReadOnly);
                bool alreadyTrusted = readRootStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
                readRootStore.Close();

                if (alreadyTrusted)
                {
                    logAction?.Invoke($"[Certificado] KMShare ({cert.Subject}) já é confiado no sistema.");
                    return true;
                }
            }
            catch { }

            return false;
        }
    }

    private static void TryEnsurePublicCertFile(string exePath, X509Certificate2 cert, Action<string>? logAction)
    {
        try
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            string cerPath = Path.Combine(dir, PublicCertFileName);
            if (!File.Exists(cerPath))
            {
                byte[] rawCert = cert.Export(X509ContentType.Cert);
                File.WriteAllBytes(cerPath, rawCert);
                logAction?.Invoke($"[Certificado] Chave pública (.cer) salva em: {cerPath}");
            }
        }
        catch { }
    }
}
