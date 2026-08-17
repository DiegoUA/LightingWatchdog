# ============================
# LightingWatchdog - Watchdog Script (v1.6)
# ============================

param(
    [switch]$Watchdog
)

# Enable popup support
Add-Type -AssemblyName System.Windows.Forms

function Show-Popup($message, $title) {
    [System.Windows.Forms.MessageBox]::Show($message, $title)
}

$logFolder = "..\logs"
if (!(Test-Path $logFolder)) {
    New-Item -ItemType Directory -Path $logFolder | Out-Null
}

function New-LogFile {
    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HH-mm-ss")
    return "$logFolder\NetworkDiag_$timestamp.txt"
}

function Rotate-Logs {
    $maxLogs = 20
    $files = Get-ChildItem $logFolder -Filter "NetworkDiag_*.txt" | Sort-Object CreationTime -Descending
    if ($files.Count -gt $maxLogs) {
        $files | Select-Object -Skip $maxLogs | Remove-Item -Force
    }
}

function Run-Diagnostics {
    Rotate-Logs
    $logFile = New-LogFile
    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HH-mm-ss")

    "=== NETWORK BUFFER DIAGNOSTICS ($timestamp) ===`n" | Out-File -FilePath $logFile

    # 1) Total TCP connections
    $tcpTotal = (Get-NetTCPConnection).Count
    "1) Total TCP connections: $tcpTotal`n" | Out-File $logFile -Append

    # 2) LightingService leak detection + auto-restart + popups
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

    # 3) Nonpaged pool monitoring
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

    # 4) WebSocket storm detection
    "4) WebSocket storm detection:" | Out-File $logFile -Append

    $initialTCP = (Get-NetTCPConnection).Count
    $initialUDP = (Get-NetUDPEndpoint).Count

    Start-Sleep -Seconds 1

    $finalTCP = (Get-NetTCPConnection).Count
    $finalUDP = (Get-NetUDPEndpoint).Count

    $tcpNew = $finalTCP - $initialTCP
    $udpNew = $finalUDP - $initialUDP

    "   New TCP connections/sec: $tcpNew" | Out-File $logFile -Append
    "   New UDP endpoints/sec: $udpNew" | Out-File $logFile -Append

    $stormThreshold = 200

    if ($tcpNew -gt $stormThreshold -or $udpNew -gt $stormThreshold) {
        $msgStorm = "WebSocket storm detected! TCP/sec: $tcpNew, UDP/sec: $udpNew"
        Show-Popup $msgStorm "LightingWatchdog Storm Alert"
        "   ALERT: $msgStorm" | Out-File $logFile -Append
    } else {
        "   Status: Normal (no storm detected)" | Out-File $logFile -Append
    }

    "`n" | Out-File $logFile -Append

    # 5) Top processes by TCP connections
    "5) Top processes by TCP connections:" | Out-File $logFile -Append
    $topTCP = Get-NetTCPConnection |
        Group-Object -Property OwningProcess |
        Sort-Object Count -Descending |
        Select-Object -First 10

    foreach ($item in $topTCP) {
        $procName = (Get-Process -Id $item.Name -ErrorAction SilentlyContinue).ProcessName
        "   PID $($item.Name) ($procName) - $($item.Count) connections" | Out-File $logFile -Append
    }
    "`n" | Out-File $logFile -Append

    # 6) TIME_WAIT count
    $timeWait = netstat -an | Select-String "TIME_WAIT" | Measure-Object
    "6) TIME_WAIT sockets: $($timeWait.Count)`n" | Out-File $logFile -Append

    # 7) Total UDP endpoints
    $udpTotal = (Get-NetUDPEndpoint).Count
    "7) Total UDP endpoints: $udpTotal`n" | Out-File $logFile -Append

    # 8) Top processes by UDP usage
    "8) Top processes by UDP usage:" | Out-File $logFile -Append
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
}

if ($Watchdog) {
    Show-Popup "LightingWatchdog started in continuous mode. Close this window or press Ctrl+C in the console to stop." "LightingWatchdog Watchdog Mode"

    while ($true) {
        Run-Diagnostics
        Start-Sleep -Seconds 60
    }
} else {
    Run-Diagnostics
}