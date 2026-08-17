Import-Module "..\\scripts\\Modules\\Utils.psm1"

function Run-Diagnostics {
    param($Config)

    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HH-mm-ss")
    $logFolder = "..\\logs"
    $exportFolder = "..\\logs\\export"

    if (!(Test-Path $exportFolder)) {
        New-Item -ItemType Directory -Path $exportFolder | Out-Null
    }

    $logFile = "$logFolder\\NetworkDiag_$timestamp.txt"

    "=== DIAGNOSTICS ($timestamp) ===`n" | Out-File $logFile

    $result = [ordered]@{
        Timestamp              = $timestamp
        TcpTotal               = $null
        LightingServicePid     = $null
        LightingServiceConns   = $null
        LightingLeakDetected   = $false
        NonPagedPercent        = $null
        NonPagedPressure       = $false
        TcpNewPerSec           = $null
        UdpNewPerSec           = $null
        StormDetected          = $false
    }

    # TCP count
    $tcpTotal = (Get-NetTCPConnection).Count
    $result.TcpTotal = $tcpTotal
    Log $logFile "TCP connections: $tcpTotal"

    # LightingService leak detection
    $proc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
    if ($proc) {
        $pid = $proc.Id
        $conns = (Get-NetTCPConnection | Where-Object { $_.OwningProcess -eq $pid }).Count

        $result.LightingServicePid   = $pid
        $result.LightingServiceConns = $conns

        Log $logFile "LightingService PID: $pid"
        Log $logFile "LightingService connections: $conns"

        if ($conns -gt $Config.LeakThreshold) {
            $result.LightingLeakDetected = $true
            Popup "LightingService leak detected ($conns connections)" "LightingWatchdog" $Config.EnablePopups
            Log $logFile "LEAK DETECTED"
        }
    } else {
        Log $logFile "LightingService not running."
    }

    # Nonpaged pool
    $os = Get-CimInstance Win32_OperatingSystem
    $np = $os.NonPagedPoolSize
    $npMax = $os.NonPagedPoolQuota
    $npPct = [math]::Round(($np / $npMax) * 100, 2)

    $result.NonPagedPercent = $npPct
    Log $logFile "Nonpaged pool: $npPct%"

    if ($npPct -gt $Config.NonPagedPoolThreshold) {
        $result.NonPagedPressure = $true
        Popup "Nonpaged pool high ($npPct%)" "Kernel Alert" $Config.EnablePopups
        Log $logFile "KERNEL PRESSURE DETECTED"
    }

    # WebSocket storm
    $tcp1 = (Get-NetTCPConnection).Count
    $udp1 = (Get-NetUDPEndpoint).Count

    Start-Sleep -Seconds 1

    $tcp2 = (Get-NetTCPConnection).Count
    $udp2 = (Get-NetUDPEndpoint).Count

    $tcpNew = $tcp2 - $tcp1
    $udpNew = $udp2 - $udp1

    $result.TcpNewPerSec = $tcpNew
    $result.UdpNewPerSec = $udpNew

    Log $logFile "TCP/sec: $tcpNew"
    Log $logFile "UDP/sec: $udpNew"

    if ($tcpNew -gt $Config.StormThreshold -or $udpNew -gt $Config.StormThreshold) {
        $result.StormDetected = $true
        Popup "WebSocket storm detected" "Storm Alert" $Config.EnablePopups
        Log $logFile "STORM DETECTED"
    }

    # JSON export
    if ($Config.EnableJsonExport) {
        $jsonPath = "$exportFolder\\diag_$timestamp.json"
        $result | ConvertTo-Json -Depth 4 | Out-File $jsonPath -Encoding UTF8
    }

    # CSV export (append)
    if ($Config.EnableCsvExport) {
        $csvPath = "$exportFolder\\diagnostics.csv"
        $obj = New-Object PSObject -Property $result

        if (!(Test-Path $csvPath)) {
            $obj | Export-Csv -Path $csvPath -NoTypeInformation
        } else {
            $obj | Export-Csv -Path $csvPath -NoTypeInformation -Append
        }
    }

    return $logFile
}

Export-ModuleMember -Function Run-Diagnostics