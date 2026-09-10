using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace WidgetSetup;

public partial class SetupWindow : Window
{
    private const string Repository = "olivierrouetcontact-lab/widget-personnalise";
    private const string ManifestUrl =
        "https://github.com/olivierrouetcontact-lab/widget-personnalise/releases/latest/download/latest.json";
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "MailWidget";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private string? _previousInstallationDirectory;
    private bool _isInstalling;

    public SetupWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _previousInstallationDirectory = FindRunningInstallationDirectory();

        if (_previousInstallationDirectory is not null)
        {
            StatusText.Text = "Le widget est encore ouvert.";
            HelpText.Text =
                "Ferme-le avec un clic droit sur son icône près de l'heure, puis choisis « Quitter ». " +
                "Reviens ensuite ici et clique sur le bouton d'installation.";
            ActionButton.Content = "J'ai fermé le widget";
        }
        else
        {
            StatusText.Text = "Prêt à installer ou réparer le widget.";
            HelpText.Text =
                "L'installation conservera le jeton Gmail, credentials.json, tes notes et tes réglages " +
                "locaux lorsqu'ils sont présents.";
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            return;
        }

        var runningDirectory = FindRunningInstallationDirectory();
        if (runningDirectory is not null)
        {
            _previousInstallationDirectory ??= runningDirectory;
            StatusText.Text = "Le widget est encore ouvert. Ferme-le d'abord.";
            HelpText.Text =
                "Clic droit sur l'icône Gmail près de l'heure → « Quitter », puis clique à nouveau ici.";
            return;
        }

        _isInstalling = true;
        ActionButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Téléchargement de la dernière version…";

        try
        {
            await InstallLatestVersionAsync();
        }
        catch (Exception ex)
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
            StatusText.Text = "L'installation a échoué.";
            HelpText.Text = ex.Message;
        }
    }

    private async Task InstallLatestVersionAsync()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "WidgetPersonnaliseSetup",
            Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "widget.zip");
        var extractDirectory = Path.Combine(tempRoot, "package");

        try
        {
            Directory.CreateDirectory(extractDirectory);

            using var manifestResponse = await _httpClient.GetAsync(ManifestUrl);
            manifestResponse.EnsureSuccessStatusCode();
            var manifestJson = await manifestResponse.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, JsonOptions)
                           ?? throw new InvalidOperationException("Le manifeste de mise à jour est invalide.");

            if (!Version.TryParse(manifest.Version, out _)
                || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
                || string.IsNullOrWhiteSpace(manifest.Sha256)
                || !IsAllowedDownload(manifest.DownloadUrl)
                || !IsSha256(manifest.Sha256))
            {
                throw new InvalidOperationException("Le manifeste de mise à jour n'est pas fiable.");
            }

            StatusText.Text = $"Téléchargement de la version {manifest.Version}…";
            using var packageResponse = await _httpClient.GetAsync(
                manifest.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);
            packageResponse.EnsureSuccessStatusCode();
            await using (var input = await packageResponse.Content.ReadAsStreamAsync())
            await using (var output = File.Create(archivePath))
            {
                await input.CopyToAsync(output);
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("La vérification de sécurité de la mise à jour a échoué.");
            }

            StatusText.Text = "Préparation de l'installation…";
            ZipFile.ExtractToDirectory(archivePath, extractDirectory, true);
            var stagedExecutable = Path.Combine(extractDirectory, "MailWidget.exe");
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidOperationException("Le paquet ne contient pas MailWidget.exe.");
            }

            var installationDirectory = GetInstallationDirectory();
            Directory.CreateDirectory(installationDirectory);
            CopyDirectoryContents(extractDirectory, installationDirectory);

            var credentialsSource = FindCredentialsSource();
            if (credentialsSource is not null)
            {
                File.Copy(
                    credentialsSource,
                    Path.Combine(installationDirectory, "credentials.json"),
                    true);
            }

            CreateShortcuts(installationDirectory);
            ConfigureStartup(installationDirectory);

            StatusText.Text = "Installation terminée. Démarrage du widget…";
            HelpText.Text = "Les raccourcis « Gmail sur le bureau » et « Gestion du widget » sont prêts.";
            ProgressBar.Visibility = Visibility.Collapsed;

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(installationDirectory, "MailWidget.exe"),
                WorkingDirectory = installationDirectory,
                UseShellExecute = true
            });

            await Task.Delay(1200);
            Close();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private string? FindCredentialsSource()
    {
        var candidateDirectories = new List<string?>()
        {
            _previousInstallationDirectory,
            GetRegistryInstallationDirectory(),
            GetShortcutInstallationDirectory(),
            GetInstallationDirectory()
        };

        foreach (var directory in candidateDirectories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => path!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory, "credentials.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindRunningInstallationDirectory()
    {
        foreach (var process in Process.GetProcessesByName("MailWidget"))
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    return Path.GetDirectoryName(executablePath);
                }
            }
            catch
            {
                // Access to a process path can be denied; try the next process.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string? GetRegistryInstallationDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(RunValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            if (value.StartsWith('"'))
            {
                var closingQuote = value.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    value = value[1..closingQuote];
                }
            }
            else
            {
                value = value.Split(' ', 2)[0];
            }

            return Path.GetDirectoryName(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetShortcutInstallationDirectory()
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "Gmail sur le bureau.lnk");
        if (!File.Exists(shortcutPath))
        {
            return null;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string targetPath = shortcut.TargetPath;

            if (Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }

            return Path.GetDirectoryName(targetPath);
        }
        catch
        {
            return null;
        }
    }

    private static string GetInstallationDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailWidget",
            "App");
    }

    private static void CopyDirectoryContents(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var targetPath = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }
    }

    private static void CreateShortcuts(string installationDirectory)
    {
        var executablePath = Path.Combine(installationDirectory, "MailWidget.exe");
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows Script Host est indisponible.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            CreateShortcut(
                shell,
                Path.Combine(desktopDirectory, "Gmail sur le bureau.lnk"),
                executablePath,
                string.Empty,
                installationDirectory,
                "Widget Gmail et pense-bête");

            CreateShortcut(
                shell,
                Path.Combine(desktopDirectory, "Gestion du widget.lnk"),
                executablePath,
                "--manage",
                installationDirectory,
                "Gérer le widget personnalisé");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void CreateShortcut(
        dynamic shell,
        string shortcutPath,
        string executablePath,
        string arguments,
        string workingDirectory,
        string description)
    {
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            shortcut.TargetPath = executablePath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Description = description;
            shortcut.Save();
        }
        finally
        {
            if (Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
    }

    private static void ConfigureStartup(string installationDirectory)
    {
        var executablePath = Path.Combine(installationDirectory, "MailWidget.exe");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
                        ?? throw new InvalidOperationException("Windows n'a pas permis le démarrage automatique.");
        key.SetValue(RunValueName, $"\"{executablePath}\"");
    }

    private static bool IsAllowedDownload(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // Temporary files can be cleaned up by Windows later.
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The window may already be closing.
        }
    }

    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosed(e);
    }
}
