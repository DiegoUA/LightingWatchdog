# ============================
# LightingWatchdog - Diagnostic Script (v1.4)
# ============================

# Enable popup support
Add-Type -AssemblyName System.Windows.Forms

function Show-Popup($message, $title) {
    [System.Windows.Forms.MessageBox]::Show($message, $title)
}

$logFolder = "..\logs"
if (!(Test-Path $logFolder)) {
    New-Item -ItemType Directory -Path $logFolder | Out-Null
}

$timestamp = (Get-Date).ToString("yyyy-MM-dd_HH-mm-ss")
$logFile = "$logFolder\NetworkDiag_$timestamp.txt"

"=== NETWORK BUFFER DIAGNOSTICS ($timestamp) ===`n" | Out-File -FilePath $logFile

# 1) Total TCP connections
$tcpTotal = (Get-NetTCPConnection).Count
"1) Total TCP connections: $tcpTotal`n" | Out-File $logFile -Append

# 2) LightingService leak detection + auto-restart + popups (v1.3)
$lightingProc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
if ($lightingProc) {
    $lightingPID = $lightingProc.Id
    $lightingConns = (Get-NetTCPConnection | Where-Object { $_.OwningProcess -eq $lightingPID }).Count

    "2) LightingService leak detection:" | Out-File $logFile -Append
    "   LightingService PID: $lightingPID" | Out-File $logFile -Append
    "   LightingService TCP connections: $lightingConns" | Out-File $logFile -Append

    $leakThreshold = 1000

    if ($lightingConns -gt $leakThreshold) {
        $msg = "LightingService leak detected! Connections: $lightingConns"
        Show-Popup $msg "LightingWatchdog Alert"
        "   POPUP: $msg" | Out-File $logFile -Append

        "   ACTION: Restarting LightingService..." | Out-File $logFile -Append

        try {
            Stop-Process -Id $lightingPID -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
            Start-Service -Name LightingService -ErrorAction Stop

            $msg2 = "LightingService restarted successfully."
            Show-Popup $msg2 "LightingWatchdog Action"
            "   RESULT: $msg2" | Out-File $logFile -Append
        }
        catch {
            $msg3 = "Failed to restart LightingService: $_"
            Show-Popup $msg3 "LightingWatchdog Error"
            "   ERROR: $msg3" | Out-File $logFile -Append
        }
    }
    else {
        "   Status: Normal (below leak threshold)" | Out-File $logFile -Append
    }
} else {
    "2) LightingService not running." | Out-File $logFile -Append
}

"`n" | Out-File $logFile -Append

# 3) Nonpaged pool monitoring (v1.4)
"3) Nonpaged pool usage:" | Out-File $logFile -Append

$osInfo = Get-CimInstance Win32_OperatingSystem
$nonPagedPool = $osInfo.NonPagedPoolSize
$nonPagedPoolMax = $osInfo.NonPagedPoolQuota
$nonPagedPercent = [math]::Round(($nonPagedPool / $nonPagedPoolMax) * 100, 2)

"   Current nonpaged pool: $nonPagedPool KB" | Out-File $logFile -Append
"   Maximum nonpaged pool: $nonPagedPoolMax KB" | Out-File $logFile -Append
"   Usage percent: $nonPagedPercent%" | Out-File $logFile -Append

$nonPagedThreshold = 70

if ($nonPagedPercent -gt $nonPagedThreshold) {
    $msgNP = "Warning: Nonpaged pool usage is high ($nonPagedPercent%). Kernel memory pressure detected."
    Show-Popup $msgNP "LightingWatchdog Kernel Alert"
    "   ALERT: $msgNP" | Out-File $logFile -Append
} else {
    "   Status: Normal (below kernel memory threshold)" | Out-File $logFile -Append
}

"`n" | Out-File $logFile -Append

# 4) Top processes by TCP connections
"4) Top processes by TCP connections:" | Out-File $logFile -Append
$topTCP = Get-NetTCPConnection |
    Group-Object -Property OwningProcess |
    Sort-Object Count -Descending |
    Select-Object -First 10

foreach ($item in $topTCP) {
    $procName = (Get-Process -Id $item.Name -ErrorAction SilentlyContinue).ProcessName
    "   PID $($item.Name) ($procName) - $($item.Count) connections" | Out-File $logFile -Append
}
"`n" | Out-File $logFile -Append

# 5) TIME_WAIT count
$timeWait = netstat -an | Select-String "TIME_WAIT" | Measure-Object
"5) TIME_WAIT sockets: $($timeWait.Count)`n" | Out-File $logFile -Append

# 6) Total UDP endpoints
$udpTotal = (Get-NetUDPEndpoint).Count
"6) Total UDP endpoints: $udpTotal`n" | Out-File $logFile -Append

# 7) Top processes by UDP usage
"7) Top processes by UDP usage:" | Out-File $logFile -Append
$topUDP = Get-NetUDPEndpoint |
    Group-Object -Property OwningProcess |
    Sort-Object Count -Descending |
    Select-Object -First 10

foreach ($item in $topUDP) {
    $procName = (Get-Process -Id $item.Name -ErrorAction SilentlyContinue).ProcessName
    "   PID $($item.Name) ($procName) - $($item.Count) UDP endpoints" | Out-File $logFile -Append
}
"`n" | Out-File $logFile -Append

"=== DIAGNOSTICS COMPLETE ===" | Out-File $logFile -Append

Get-Content $logFile