$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDirectory

function Refresh-Path {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machinePath;$userPath"
}

function Get-DotnetCandidates {
    $candidates = @()
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    $candidates += @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )

    return @($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
}

function Has-UsableSdk([string] $dotnetExecutable) {
    if ([string]::IsNullOrWhiteSpace($dotnetExecutable) -or -not (Test-Path $dotnetExecutable)) {
        return $false
    }

    try {
        $sdkList = @(& $dotnetExecutable --list-sdks 2>$null)
        foreach ($sdk in $sdkList) {
            if ($sdk -match '^\s*(\d+)\.') {
                # Any SDK 8 or newer can build this net8.0 project.
                if ([int]$Matches[1] -ge 8) {
                    return $true
                }
            }
        }
    }
    catch {
        return $false
    }

    return $false
}

function Find-UsableDotnet {
    foreach ($candidate in (Get-DotnetCandidates)) {
        if (Has-UsableSdk $candidate) {
            return $candidate
        }
    }

    return $null
}

function Get-ExistingWidgetProcesses {
    # Use the process name rather than its path. Windows Defender and some
    # WPF/desktop-host combinations can hide or delay the executable path,
    # even though the process still holds the published file open.
    return @(Get-Process -Name "MailWidget" -ErrorAction SilentlyContinue)
}

function Stop-ExistingWidgets {
    $runningProcesses = @(Get-ExistingWidgetProcesses)
    foreach ($process in $runningProcesses) {
        Write-Host "Fermeture de l'ancien widget..." -ForegroundColor Yellow
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(10)
    $stillRunning = @()
    do {
        $stillRunning = @(Get-ExistingWidgetProcesses)
        if ($stillRunning.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Une ancienne instance de MailWidget.exe est encore en cours d'utilisation. Ferme-la depuis le Gestionnaire des tâches, puis relance l'installation."
}

Refresh-Path
$dotnetPath = Find-UsableDotnet
if (-not $dotnetPath) {
    Write-Host "Le SDK .NET 8 est necessaire pour preparer le widget." -ForegroundColor Yellow

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"
        throw "Installe le SDK .NET 8, puis relance Installer-Windows.cmd."
    }

    # --force repairs/re-runs the installer when winget reports that the same
    # package is already installed but the SDK is not visible to dotnet.
    winget install --id Microsoft.DotNet.SDK.8 --exact --force --accept-source-agreements --accept-package-agreements
    Refresh-Path
    $dotnetPath = Find-UsableDotnet
    if (-not $dotnetPath) {
        Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"
        throw "Le SDK .NET 8 n'est pas visible. Installe le SDK x64 depuis la page ouverte, puis relance Installer-Windows.cmd."
    }
}

$publishRootDirectory = Join-Path $projectDirectory "bin\Release\net8.0-windows\win-x64"
$publishDirectory = Join-Path $publishRootDirectory ("publish-" + [Guid]::NewGuid().ToString("N"))
$executablePath = Join-Path $publishDirectory "MailWidget.exe"
Stop-ExistingWidgets

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

Write-Host "Preparation du fichier executable..." -ForegroundColor Cyan

& $dotnetPath publish (Join-Path $projectDirectory "MailWidget.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishDir=$publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "La preparation du widget a echoue."
}

$credentialsSource = Join-Path $projectDirectory "credentials.json"
$credentialsTarget = Join-Path $publishDirectory "credentials.json"
if (Test-Path $credentialsSource) {
    Copy-Item $credentialsSource $credentialsTarget -Force
}

if (-not (Test-Path $executablePath)) {
    throw "MailWidget.exe n'a pas ete trouve apres la preparation."
}

$desktopDirectory = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktopDirectory "Gmail sur le bureau.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $publishDirectory
$shortcut.IconLocation = "$executablePath,0"
$shortcut.Description = "Widget Gmail et pense-bete"
$shortcut.Save()

if (-not (Test-Path $credentialsTarget)) {
    Write-Host "Le widget est installe, mais credentials.json manque encore pour Gmail." -ForegroundColor Yellow
    Write-Host "Ajoute ce fichier dans : $publishDirectory" -ForegroundColor Yellow
}

Write-Host "Raccourci cree sur le bureau : $shortcutPath" -ForegroundColor Green
Start-Process -FilePath $executablePath -WorkingDirectory $publishDirectory
