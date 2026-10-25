# start_control_center.ps1 - Launch Shittim Control Center UI
$baseDir = $PSScriptRoot
$controlCenterDir = Join-Path $baseDir "ShittimControlCenter"

Write-Host "Starting Shittim Control Center..." -ForegroundColor Cyan

# Set environment paths so Control Center can locate dotnet 10 and mitmweb
$userDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet"
if (Test-Path "$userDotnet\dotnet.exe") {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
}
$localBin = Join-Path (Split-Path $baseDir -Parent) "bin"
if (Test-Path "$localBin\mitmweb.exe") {
    $env:PATH = "$localBin;$env:PATH"
}

Start-Process "cmd.exe" -ArgumentList "/c cd /d `"$controlCenterDir`" && pnpm start" -WindowStyle Hidden

Write-Host "Shittim Control Center UI launched!" -ForegroundColor Green
