param(
    [Parameter(Mandatory = $true)]
    [string] $PackageUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $Sha256,

    [Parameter(Mandatory = $true)]
    [int] $CurrentProcessId,

    [Parameter(Mandatory = $true)]
    [string] $CurrentExecutable
)

$ErrorActionPreference = "Stop"

function Show-UpdateError([string] $message) {
    Write-Host $message -ForegroundColor Red
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            $message,
            "Widget personnalisé",
            [System.Windows.MessageBoxButton]::OK,
            [System.Windows.MessageBoxImage]::Error) | Out-Null
    }
    catch {
        # The console message remains available if WPF cannot be loaded.
    }
}

$updateRoot = Join-Path $env:TEMP "WidgetPersonnalise"
$updateDirectory = Join-Path $updateRoot ("update-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $updateDirectory "widget-update.zip"
$extractDirectory = Join-Path $updateDirectory "package"

try {
    New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null

    Write-Host "Téléchargement de la mise à jour..."
    Invoke-WebRequest -Uri $PackageUrl -OutFile $archivePath -UseBasicParsing

    $actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ine $Sha256) {
        throw "La vérification de sécurité de la mise à jour a échoué."
    }

    Expand-Archive -Path $archivePath -DestinationPath $extractDirectory -Force
    $newExecutableInPackage = Join-Path $extractDirectory "MailWidget.exe"
    if (-not (Test-Path $newExecutableInPackage)) {
        throw "Le paquet téléchargé ne contient pas MailWidget.exe."
    }

    $currentPath = (Resolve-Path $CurrentExecutable).Path
    $currentDirectory = Split-Path -Parent $currentPath
    $installationRoot = Split-Path -Parent $currentDirectory
    $newDirectory = Join-Path $installationRoot ("publish-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $newDirectory -Force | Out-Null

    Copy-Item -Path (Join-Path $extractDirectory "*") -Destination $newDirectory -Recurse -Force

    $oldCredentials = Join-Path $currentDirectory "credentials.json"
    if (Test-Path $oldCredentials) {
        Copy-Item -Path $oldCredentials -Destination (Join-Path $newDirectory "credentials.json") -Force
    }

    $deadline = (Get-Date).AddSeconds(30)
    while (Get-Process -Id $CurrentProcessId -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) {
            throw "Le widget actuel ne s'est pas fermé à temps."
        }
        Start-Sleep -Milliseconds 250
    }

    $newExecutable = Join-Path $newDirectory "MailWidget.exe"
    $desktopDirectory = [Environment]::GetFolderPath("Desktop")
    $shortcutPath = Join-Path $desktopDirectory "Gmail sur le bureau.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $newExecutable
    $shortcut.WorkingDirectory = $newDirectory
    $shortcut.IconLocation = "$newExecutable,0"
    $shortcut.Description = "Widget Gmail et pense-bête"
    $shortcut.Save()

    $runKeyPath = "Software\Microsoft\Windows\CurrentVersion\Run"
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath, $true)
    if ($runKey -and $null -ne $runKey.GetValue("MailWidget", $null)) {
        $runKey.SetValue("MailWidget", "`"$newExecutable`"")
    }
    if ($runKey) {
        $runKey.Dispose()
    }

    Start-Process -FilePath $newExecutable -WorkingDirectory $newDirectory
}
catch {
    Show-UpdateError $_.Exception.Message
    exit 1
}
finally {
    if (Test-Path $updateDirectory) {
        Remove-Item -Path $updateDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
