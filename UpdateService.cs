using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailWidget;

public sealed record WidgetUpdate(
    string VersionText,
    string DownloadUrl,
    string Sha256,
    string? Notes)
{
    public Version Version => System.Version.Parse(VersionText);
}

public sealed class UpdateService
{
    public const string ManifestUrl =
        "https://github.com/olivierrouetcontact-lab/widget-personnalise/releases/latest/download/latest.json";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? new Version(1, 0, 0)
                : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    public async Task<WidgetUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(ManifestUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
                stream,
                JsonOptions,
                cancellationToken);

            if (manifest is null
                || !Version.TryParse(manifest.Version, out var remoteVersion)
                || remoteVersion.CompareTo(CurrentVersion) <= 0
                || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
                || string.IsNullOrWhiteSpace(manifest.Sha256)
                || !IsAllowedDownload(manifest.DownloadUrl)
                || !IsSha256(manifest.Sha256))
            {
                return null;
            }

            return new WidgetUpdate(
                remoteVersion.ToString(3),
                manifest.DownloadUrl,
                manifest.Sha256.ToLowerInvariant(),
                manifest.Notes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An unavailable update server must never prevent the Gmail widget
            // from starting or checking mail.
            return null;
        }
    }

    public bool TryStartInstaller(WidgetUpdate update, out string error)
    {
        error = string.Empty;

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Update-Windows.ps1");
        var currentExecutable = Environment.ProcessPath;
        if (!File.Exists(scriptPath))
        {
            error = "Le module de mise à jour n'est pas présent dans cette installation.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
        {
            error = "Le chemin de l'application est introuvable.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-PackageUrl");
        startInfo.ArgumentList.Add(update.DownloadUrl);
        startInfo.ArgumentList.Add("-Sha256");
        startInfo.ArgumentList.Add(update.Sha256);
        startInfo.ArgumentList.Add("-CurrentProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-CurrentExecutable");
        startInfo.ArgumentList.Add(currentExecutable);

        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WidgetPersonnalise/1.0");
        return client;
    }

    private static bool IsAllowedDownload(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
        public string? Notes { get; set; }
    }
}
