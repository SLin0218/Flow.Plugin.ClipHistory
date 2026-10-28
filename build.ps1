[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = "C:\Users\USER\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "==> Building Flow.Plugin.ClipHistory (Release)..." -ForegroundColor Cyan
& $dotnet build (Join-Path $scriptDir "Flow.Plugin.ClipHistory.csproj") -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "==> Build succeeded!" -ForegroundColor Green

if ($Deploy -or $Restart) {
    Write-Host "==> Stopping Flow Launcher if running..." -ForegroundColor Cyan
    Get-Process -Name "Flow.Launcher" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if ($Deploy) {
    $targetDir = "C:\Users\USER\scoop\persist\Flow-Launcher\UserData\Plugins\ClipHistory-1.0.0"
    Write-Host "==> Deploying to $targetDir..." -ForegroundColor Cyan

    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    }

    $sourceDir = Join-Path $scriptDir "bin\Release"
    Copy-Item -Path "$sourceDir\*" -Destination $targetDir -Recurse -Force
    Write-Host "==> Deployment completed!" -ForegroundColor Green
}

if ($Restart) {
    Write-Host "==> Restarting Flow Launcher..." -ForegroundColor Cyan
    $flowExe = "C:\Users\USER\scoop\apps\Flow-Launcher\2.1.4\Flow.Launcher.exe"
    if (-not (Test-Path $flowExe)) {
        $flowExe = "C:\Users\USER\scoop\apps\Flow-Launcher\current\Flow.Launcher.exe"
    }
    if (Test-Path $flowExe) {
        & explorer.exe "$flowExe"
        Write-Host "==> Flow Launcher restarted successfully!" -ForegroundColor Green
    } else {
        Write-Warning "Flow.Launcher.exe not found at $flowExe"
    }
}
