namespace KMShare.Security;

/// <summary>
/// Referência e utilitário para assinatura digital e chaves públicas do KMShare.
/// Nenhuma operação de gravação ou inspeção no repositório de certificados do Windows (Root/CA/AuthRoot)
/// é executada em tempo de execução para evitar falsos positivos de regras comportamentais (Sigma / EDR).
/// </summary>
public static class WindowsAuthenticodeTrust
{
    public const string PublicCertFileName = "KMShare-CodeSigning.cer";

    /// <summary>
    /// Nenhuma operação em repositório de certificados ou escrita em disco é executada em runtime.
    /// O certificado é fornecido avulso na página de releases para instalação manual opcional pelo usuário.
    /// </summary>
    public static bool EnsureSelfTrust(Action<string>? logAction = null)
    {
        return false;
    }
}
