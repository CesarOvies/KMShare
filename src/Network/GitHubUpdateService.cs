using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KMShare.Platform;
using KMShare.UI;

namespace KMShare.Network;

public sealed record GitHubReleaseAsset(
    string Name,
    long Size,
    string BrowserDownloadUrl,
    string ApiUrl,
    long Id);

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string ReleaseNotes,
    List<GitHubReleaseAsset> Assets);

/// <summary>
/// Serviço de verificação de atualizações no GitHub e download seguro do instalador em User-Space.
/// </summary>
public static class GitHubUpdateService
{
    public const string GitHubRepo = "CesarOvies/KMShare";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"KMShare-App/{AppVersionInfo.CurrentVersion}");
        return client;
    }

    public static string? TryGetGitHubToken(KMShareUserConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(config?.GitHubToken))
            return config.GitHubToken.Trim();

        string? env = Environment.GetEnvironmentVariable("KM_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        env = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        env = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        // Fallback para CLI do GitHub (gh) autenticada localmente
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                string token = proc.StandardOutput.ReadToEnd().Trim();
                if (proc.WaitForExit(2000) && proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }
        catch
        {
            // gh CLI indisponível
        }

        return null;
    }

    public static bool IsVersionNewer(string latestTagOrVersion, string currentVersion)
    {
        string cleanLatest = latestTagOrVersion.TrimStart('v', 'V').Trim();
        string cleanCurrent = currentVersion.TrimStart('v', 'V').Trim();

        int plus = cleanLatest.IndexOf('+');
        if (plus > 0) cleanLatest = cleanLatest[..plus];
        plus = cleanCurrent.IndexOf('+');
        if (plus > 0) cleanCurrent = cleanCurrent[..plus];

        string baseLatest = cleanLatest;
        int dash = baseLatest.IndexOf('-');
        if (dash > 0) baseLatest = baseLatest[..dash];

        string baseCurrent = cleanCurrent;
        dash = baseCurrent.IndexOf('-');
        if (dash > 0) baseCurrent = baseCurrent[..dash];

        if (Version.TryParse(baseLatest, out var vLatest) && Version.TryParse(baseCurrent, out var vCurrent))
        {
            return vLatest > vCurrent;
        }

        return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        KMShareUserConfig? config = null,
        CancellationToken ct = default)
    {
        string currentVer = AppVersionInfo.CurrentVersion;
        string? token = TryGetGitHubToken(config);

        // 1. Tentar via GitHub REST API (HTTPS)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                var result = ParseReleaseJson(json, currentVer);
                if (result != null) return result;
            }
        }
        catch
        {
            // Falha de rede ou timeout
        }

        // 2. Fallback: tentar consulta estruturada via 'gh release view' se instalado
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "release view --json tagName,url,body,assets",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                string json = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(json))
                {
                    var result = ParseReleaseJson(json, currentVer);
                    if (result != null) return result;
                }
            }
        }
        catch
        {
            // gh CLI indisponível
        }

        return new UpdateCheckResult(
            HasUpdate: false,
            CurrentVersion: currentVer,
            LatestVersion: currentVer,
            ReleaseUrl: $"https://github.com/{GitHubRepo}/releases",
            ReleaseNotes: string.Empty,
            Assets: []);
    }

    public static UpdateCheckResult? ParseReleaseJson(string json, string currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tProp)
                ? tProp.GetString() ?? ""
                : (root.TryGetProperty("tagName", out var tnProp) ? tnProp.GetString() ?? "" : "");

            if (string.IsNullOrWhiteSpace(tagName)) return null;

            string url = root.TryGetProperty("html_url", out var uProp)
                ? uProp.GetString() ?? ""
                : (root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "");

            string body = root.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var aArray) && aArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in aArray.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    string browserUrl = a.TryGetProperty("browser_download_url", out var bu)
                        ? bu.GetString() ?? ""
                        : (a.TryGetProperty("url", out var fallbackUrl) ? fallbackUrl.GetString() ?? "" : "");
                    string apiUrl = a.TryGetProperty("url", out var au)
                        ? au.GetString() ?? ""
                        : (a.TryGetProperty("apiUrl", out var fApiUrl) ? fApiUrl.GetString() ?? "" : "");
                    long id = a.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt64()
                        : 0;

                    assets.Add(new GitHubReleaseAsset(name, size, browserUrl, apiUrl, id));
                }
            }

            string cleanTag = tagName.TrimStart('v', 'V');
            bool isNewer = IsVersionNewer(cleanTag, currentVersion);

            return new UpdateCheckResult(
                HasUpdate: isNewer,
                CurrentVersion: currentVersion,
                LatestVersion: cleanTag,
                ReleaseUrl: url,
                ReleaseNotes: body,
                Assets: assets);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(
        UpdateCheckResult update,
        KMShareUserConfig? config = null,
        Action<string>? progressLogger = null,
        CancellationToken ct = default)
    {
        try
        {
            string? token = TryGetGitHubToken(config);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var asset = update.Assets.FirstOrDefault(a => a.Name.EndsWith("-win-x64-installer.exe", StringComparison.OrdinalIgnoreCase))
                         ?? update.Assets.FirstOrDefault(a => a.Name.EndsWith("-win-x64-portable.zip", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    progressLogger?.Invoke("Nenhum executável Windows encontrado. Abrindo página de releases no navegador...");
                    OpenUrlInBrowser(update.ReleaseUrl);
                    return true;
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "KMShare_Update");
                Directory.CreateDirectory(tempDir);
                string targetFile = Path.Combine(tempDir, asset.Name);

                progressLogger?.Invoke($"Baixando {asset.Name} ({(asset.Size > 0 ? (asset.Size / 1024.0 / 1024.0).ToString("F1") + " MB" : "...")})...");

                bool downloaded = await DownloadAssetToFileAsync(asset, update.LatestVersion, targetFile, token, progressLogger, ct);
                if (!downloaded || !File.Exists(targetFile))
                {
                    progressLogger?.Invoke("Falha no download do pacote de atualização.");
                    return false;
                }

                progressLogger?.Invoke("Download concluído com sucesso.");

                var checksumAsset = update.Assets.FirstOrDefault(a => a.Name.Equals("CHECKSUMS.sha256", StringComparison.OrdinalIgnoreCase));
                if (checksumAsset != null)
                {
                    string checksumFile = Path.Combine(tempDir, "CHECKSUMS.sha256");
                    if (await DownloadAssetToFileAsync(checksumAsset, update.LatestVersion, checksumFile, token, null, ct))
                    {
                        if (VerifySha256Checksum(targetFile, checksumFile))
                        {
                            progressLogger?.Invoke("Integridade criptográfica SHA-256 verificada e intacta.");
                        }
                        else
                        {
                            progressLogger?.Invoke("[AVISO] Divergência no hash SHA-256 do instalador.");
                        }
                    }
                }

                progressLogger?.Invoke("Iniciando instalador do KMShare e encerrando instância atual...");

                var psi = new ProcessStartInfo
                {
                    FileName = targetFile,
                    UseShellExecute = true
                };

                Process.Start(psi);
                Environment.Exit(0);
                return true;
            }
            else
            {
                progressLogger?.Invoke($"Abrindo release v{update.LatestVersion} no navegador...");
                OpenUrlInBrowser(update.ReleaseUrl);
                return true;
            }
        }
        catch (Exception ex)
        {
            progressLogger?.Invoke($"Erro ao atualizar: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> DownloadAssetToFileAsync(
        GitHubReleaseAsset asset,
        string releaseTag,
        string destinationFilePath,
        string? token,
        Action<string>? progressLogger,
        CancellationToken ct)
    {
        // 1. Tentar primeiro via gh CLI se disponível
        try
        {
            string outDir = Path.GetDirectoryName(destinationFilePath)!;
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"release download v{releaseTag} -p \"{asset.Name}\" --dir \"{outDir}\" --clobber",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(destinationFilePath))
                {
                    return true;
                }
            }
        }
        catch
        {
            // gh CLI indisponível
        }

        // 2. Download direto via HttpClient
        try
        {
            string downloadUrl = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(asset.ApiUrl)
                ? asset.ApiUrl
                : asset.BrowserDownloadUrl;

            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.UserAgent.ParseAdd($"KMShare-App/{AppVersionInfo.CurrentVersion}");
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            }

            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) && downloadUrl != asset.BrowserDownloadUrl)
            {
                using var req2 = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
                req2.Headers.UserAgent.ParseAdd($"KMShare-App/{AppVersionInfo.CurrentVersion}");
                using var resp2 = await HttpClient.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
                resp2.EnsureSuccessStatusCode();
                await using var fs2 = File.Create(destinationFilePath);
                await resp2.Content.CopyToAsync(fs2, ct);
                return true;
            }

            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(destinationFilePath);
            await resp.Content.CopyToAsync(fs, ct);
            return true;
        }
        catch (Exception ex)
        {
            progressLogger?.Invoke($"Download HTTP falhou: {ex.Message}");
            return false;
        }
    }

    private static bool VerifySha256Checksum(string targetFile, string checksumsFile)
    {
        try
        {
            string fileName = Path.GetFileName(targetFile);
            string[] lines = File.ReadAllLines(checksumsFile);
            string? expectedHash = null;
            foreach (var line in lines)
            {
                if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        expectedHash = parts[0].Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(expectedHash)) return false;

            byte[] actualHashBytes = SHA256.HashData(File.ReadAllBytes(targetFile));
            string actualHash = Convert.ToHexString(actualHashBytes);

            return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
