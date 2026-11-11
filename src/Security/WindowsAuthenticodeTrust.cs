using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MKShare.Security;

/// <summary>
/// Gerencia a auto-verificação e instalação segura da chave pública do certificado de assinatura
/// de código do KMShare no repositório de Usuário do Windows (CurrentUser\Root e CurrentUser\TrustedPublisher).
/// </summary>
public static class WindowsAuthenticodeTrust
{
    public const string PublicCertFileName = "KMShare-CodeSigning.cer";

    /// <summary>
    /// Garante que o certificado com o qual este binário foi assinado esteja confiado
    /// no escopo do usuário atual, evitando avisos futuros do SmartScreen e do UAC em atualizações.
    /// Também garante que uma cópia pública (.cer) esteja disponível na pasta para instalação manual.
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
            // 1. Sempre disponibiliza a cópia da chave pública (.cer) ao lado do executável para instalação manual
            TryEnsurePublicCertFile(exePath, cert, logAction);

            // 2. Verifica se o certificado já é confiado na raiz do usuário atual
            try
            {
                using var readRootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                readRootStore.Open(OpenFlags.ReadOnly);
                bool alreadyTrusted = readRootStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
                readRootStore.Close();

                if (alreadyTrusted)
                {
                    return true;
                }
            }
            catch { }

            // 3. Adiciona o certificado às Autoridades Raiz e Editores Confiáveis do Usuário
            try
            {
                using (var writeRootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    writeRootStore.Open(OpenFlags.ReadWrite);
                    writeRootStore.Add(cert);
                    writeRootStore.Close();
                }

                using (var pubStore = new X509Store(StoreName.TrustedPublisher, StoreLocation.CurrentUser))
                {
                    pubStore.Open(OpenFlags.ReadWrite);
                    if (pubStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
                    {
                        pubStore.Add(cert);
                    }
                    pubStore.Close();
                }

                logAction?.Invoke($"[Certificado] KMShare ({cert.Subject}) registrado como confiável no Windows.");
                return true;
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[Certificado] Auto-confirmação do certificado ignorada ou recusada: {ex.Message}");
                return false;
            }
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
