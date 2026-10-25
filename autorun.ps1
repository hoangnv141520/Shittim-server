$baseDir = $PSScriptRoot

# Ensure .NET 10 SDK takes precedence
$userDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet"
if (Test-Path "$userDotnet\dotnet.exe") {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
}

# Auto-detect local bin folder and ensure PATH is loaded in this session
$localBin = Join-Path (Split-Path $baseDir -Parent) "bin"
if ((Test-Path "$localBin\mitmweb.exe") -and (-not $env:PATH.Contains($localBin))) {
    $env:PATH = "$localBin;$env:PATH"
}
$userScripts = "$env:APPDATA\Python\Python313\Scripts"
if ((Test-Path "$userScripts\mitmweb.exe") -and (-not $env:PATH.Contains($userScripts))) {
    $env:PATH = "$userScripts;$env:PATH"
}

$dotnetVersions = dotnet --list-sdks | Select-String "^[6-9]|^1[0-9]" 2>&1
$hasDotnet = -not [System.String]::IsNullOrEmpty($dotnetVersions)
if ($LASTEXITCODE -ne 0 -or -not $hasDotnet) {
    Write-Host "Error: .NET SDK 6+ is not installed or not found in PATH" -ForegroundColor Red
    Write-Host "Please install .NET SDK from https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

if (-not (Get-Command mitmweb -ErrorAction SilentlyContinue)) {
    Write-Host "Error: mitmweb is not installed or not found in PATH" -ForegroundColor Red
    Write-Host "Please install mitmproxy tools from https://mitmproxy.org or ensure bin folder is in PATH" -ForegroundColor Yellow
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

Write-Host "Starting Shittim Server..." -ForegroundColor Cyan
Start-Sleep -Seconds 1

Write-Host "Starting game server..." -ForegroundColor Green
$serverCmd = "set `"DOTNET_ROOT=$userDotnet`" && set `"PATH=$userDotnet;$localBin;$userScripts;%PATH%`" && cd /d `"$baseDir\Shittim-Server`" && dotnet run --project Shittim-Server.csproj"
Start-Process cmd -ArgumentList "/k", $serverCmd -WindowStyle Normal

Start-Sleep -Seconds 2
Write-Host "Starting mitmproxy..." -ForegroundColor Green
$proxyCmd = "set `"PATH=$localBin;$userScripts;%PATH%`" && cd /d `"$baseDir\Scripts\redirect_server_mitmproxy`" && mitmweb -m wireguard --no-http2 -s redirect_server.py --set termlog_verbosity=warn --mode local:BlueArchive.exe"
Start-Process cmd -ArgumentList "/k", $proxyCmd -WindowStyle Normal

Write-Host ""
Write-Host "Server started successfully!" -ForegroundColor Green
Write-Host "- Game server: http://localhost:5000 (Swagger API: http://localhost:5000/swagger)" -ForegroundColor Yellow
Write-Host "- Mitmproxy web: http://127.0.0.1:8081" -ForegroundColor Yellow
Write-Host "- Control Panel GUI: run .\start_control_center.bat" -ForegroundColor Yellow
Write-Host "- To stop all servers cleanly: run .\stop.ps1 (or stop.bat)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Launch Blue Archive to connect." -ForegroundColor Green