# stop.ps1 - Cleanly terminate Shittim Server and proxy processes
Write-Host "Stopping Shittim Server and Mitmproxy..." -ForegroundColor Cyan

$targets = @("mitmweb", "mitmdump", "mitmproxy", "Shittim-Server")
foreach ($name in $targets) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "Terminating $name (PID: $(($procs | Select-Object -ExpandProperty Id) -join ', '))..." -ForegroundColor Yellow
        Stop-Process -Name $name -Force -ErrorAction SilentlyContinue
    }
}

# Also find dotnet processes running Shittim-Server
$dotnetProcs = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*Shittim-Server*" }
foreach ($p in $dotnetProcs) {
    Write-Host "Terminating dotnet game server (PID: $($p.ProcessId))..." -ForegroundColor Yellow
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

# Reset WinHTTP proxy and flush DNS to restore clean network state
Write-Host "Resetting network proxy state..." -ForegroundColor Cyan
netsh winhttp reset proxy | Out-Null
ipconfig /flushdns | Out-Null

Write-Host "All Shittim Server and proxy processes have been stopped." -ForegroundColor Green
