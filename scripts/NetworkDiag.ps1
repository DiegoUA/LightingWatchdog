# ============================
# LightingWatchdog - Diagnostic Script (v1.1)
# ============================

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

# 2) LightingService leak detection
$lightingProc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
if ($lightingProc) {
    $lightingPID = $lightingProc.Id
    $lightingConns = (Get-NetTCPConnection | Where-Object { $_.OwningProcess -eq $lightingPID }).Count

    "2) LightingService leak detection:" | Out-File $logFile -Append
    "   LightingService PID: $lightingPID" | Out-File $logFile -Append
    "   LightingService TCP connections: $lightingConns" | Out-File $logFile -Append

    if ($lightingConns -gt 1000) {
        "   LEAK DETECTED: LightingService exceeded 1000 connections." | Out-File $logFile -Append
    } else {
        "   Status: Normal (below leak threshold)" | Out-File $logFile -Append
    }
} else {
    "2) LightingService not running." | Out-File $logFile -Append
}

"`n" | Out-File $logFile -Append

# 3) Top processes by TCP connections
"3) Top processes by TCP connections:" | Out-File $logFile -Append
$topTCP = Get-NetTCPConnection |
    Group-Object -Property OwningProcess |
    Sort-Object Count -Descending |
    Select-Object -First 10

foreach ($item in $topTCP) {
    $procName = (Get-Process -Id $item.Name -ErrorAction SilentlyContinue).ProcessName
    "   PID $($item.Name) ($procName) - $($item.Count) connections" | Out-File $logFile -Append
}
"`n" | Out-File $logFile -Append

# 4) TIME_WAIT count
$timeWait = netstat -an | Select-String "TIME_WAIT" | Measure-Object
"4) TIME_WAIT sockets: $($timeWait.Count)`n" | Out-File $logFile -Append

# 5) Total UDP endpoints
$udpTotal = (Get-NetUDPEndpoint).Count
"5) Total UDP endpoints: $udpTotal`n" | Out-File $logFile -Append

# 6) Top processes by UDP usage
"6) Top processes by UDP usage:" | Out-File $logFile -Append
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