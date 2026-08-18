Import-Module "..\\scripts\\Modules\\Utils.psm1"
Import-Module "..\\scripts\\Modules\\Trends.psm1"

function Measure-HealthScore {
    param(
        $Result,
        $Config
    )

    $score = 100
    $w = $Config.Weights

    if ($Result.LightingLeakDetected) {
        $score -= $w.LightingLeak
    }

    if ($Result.NonPagedPressure) {
        $score -= $w.NonPagedPressure
    }

    if ($Result.StormDetected) {
        $score -= $w.Storm
    }

    if ($Result.TcpTotal -gt $Config.LeakThreshold) {
        $score -= $w.TcpLoad
    }

    if ($Result.UdpNewPerSec -gt $Config.StormThreshold) {
        $score -= $w.UdpLoad
    }

    if ($score -lt 0) { $score = 0 }
    if ($score -gt 100) { $score = 100 }

    return $score
}

function Invoke-Diagnostics {
    param(
        $Config
    )

    $timestamp    = (Get-Date).ToString("yyyy-MM-dd_HH-mm-ss")
    $logFolder    = "..\\logs"
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
        HealthScore            = $null
        RollingAverage         = $null
        RollingStdDev          = $null
        ZScore                 = $null
        TrendDirection         = $null
    }

    # 1) TCP count
    $tcpTotal = (Get-NetTCPConnection).Count
    $result.TcpTotal = $tcpTotal
    Write-Log $logFile "TCP connections: $tcpTotal"

    # 2) LightingService leak detection
    $proc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
    if ($proc) {
        $pid   = $proc.Id
        $conns = (Get-NetTCPConnection | Where-Object { $_.OwningProcess -eq $pid }).Count

        $result.LightingServicePid   = $pid
        $result.LightingServiceConns = $conns

        Write-Log $logFile "LightingService PID: $pid"
        Write-Log $logFile "LightingService connections: $conns"

        if ($conns -gt $Config.LeakThreshold) {
            $result.LightingLeakDetected = $true
            Show-Alert "LightingService leak detected ($conns connections)" "LightingWatchdog" $Config.EnablePopups
            Write-Log $logFile "LEAK DETECTED"
        }
    } else {
        Write-Log $logFile "LightingService not running."
    }

    # 3) Nonpaged pool (safe)
    $os   = Get-CimInstance Win32_OperatingSystem
    $np   = [double]$os.NonPagedPoolSize
    $npMax = [double]$os.NonPagedPoolQuota

    if (-not $npMax -or $npMax -le 0 -or $npMax -lt $np) {
        Write-Log $logFile "WARNING: NonPagedPoolQuota was 0 or invalid. Using fallback value."
        $npMax = [math]::Max($np, 1) * 2
    }

    $npPct = [math]::Round(($np / $npMax) * 100, 2)
    $result.NonPagedPercent = $npPct

    Write-Log $logFile "Nonpaged pool: $npPct%"

    if ($npPct -gt $Config.NonPagedPoolThreshold) {
        $result.NonPagedPressure = $true
        Show-Alert "Nonpaged pool high ($npPct%)" "Kernel Alert" $Config.EnablePopups
        Write-Log $logFile "KERNEL PRESSURE DETECTED"
    }

    # 4) WebSocket storm
    $tcp1 = (Get-NetTCPConnection).Count
    $udp1 = (Get-NetUDPEndpoint).Count

    Start-Sleep -Seconds 1

    $tcp2 = (Get-NetTCPConnection).Count
    $udp2 = (Get-NetUDPEndpoint).Count

    $tcpNew = $tcp2 - $tcp1
    $udpNew = $udp2 - $udp1

    $result.TcpNewPerSec = $tcpNew
    $result.UdpNewPerSec = $udpNew

    Write-Log $logFile "TCP/sec: $tcpNew"
    Write-Log $logFile "UDP/sec: $udpNew"

    if ($tcpNew -gt $Config.StormThreshold -or $udpNew -gt $Config.StormThreshold) {
        $result.StormDetected = $true
        Show-Alert "WebSocket storm detected" "Storm Alert" $Config.EnablePopups
        Write-Log $logFile "STORM DETECTED"
    }

    # 5) Health score
    $result.HealthScore = Measure-HealthScore -Result $result -Config $Config
    Write-Log $logFile "Health Score: $($result.HealthScore)"

    # 6) Trend analysis
    $trendCsvPath = "$exportFolder\\HealthTrend.csv"
    $trendData    = Get-TrendData -CsvPath $trendCsvPath -Window $Config.TrendWindow
    $trend        = Measure-Trend -CurrentScore $result.HealthScore -TrendData $trendData

    $result.RollingAverage = $trend.RollingAverage
    $result.RollingStdDev  = $trend.RollingStdDev
    $result.ZScore         = $trend.ZScore
    $result.TrendDirection = $trend.TrendDirection

    Write-Log $logFile "Trend: Avg=$($trend.RollingAverage), StdDev=$($trend.RollingStdDev), Z=$($trend.ZScore), Dir=$($trend.TrendDirection)"

    if ($Config.EnablePredictiveAlerts) {
        if ([math]::Abs($trend.ZScore) -ge 2) {
            Write-Log $logFile "ANOMALY: Health score deviates significantly from trend (Z=$($trend.ZScore))."
            Show-Alert "Health anomaly detected (Z=$($trend.ZScore)). Trend: $($trend.TrendDirection)." "LightingWatchdog Trend" $Config.EnablePopups
        }
    }

    # 7) JSON export
    if ($Config.EnableJsonExport) {
        $jsonPath = "$exportFolder\\diag_$timestamp.json"
        $result | ConvertTo-Json -Depth 4 | Out-File $jsonPath -Encoding UTF8
    }

    # 8) CSV export (diagnostics)
    if ($Config.EnableCsvExport) {
        $csvPath = "$exportFolder\\diagnostics.csv"
        $obj = New-Object PSObject -Property $result

        if (!(Test-Path $csvPath)) {
            $obj | Export-Csv -Path $csvPath -NoTypeInformation
        } else {
            $obj | Export-Csv -Path $csvPath -NoTypeInformation -Append
        }
    }

    # 9) HealthTrend.csv export
    $trendRow = New-Object PSObject -Property @{
        Timestamp      = $result.Timestamp
        HealthScore    = $result.HealthScore
        RollingAverage = $result.RollingAverage
        RollingStdDev  = $result.RollingStdDev
        ZScore         = $result.ZScore
        TrendDirection = $result.TrendDirection
    }

    if (!(Test-Path $trendCsvPath)) {
        $trendRow | Export-Csv -Path $trendCsvPath -NoTypeInformation
    } else {
        $trendRow | Export-Csv -Path $trendCsvPath -NoTypeInformation -Append
    }

    return $logFile
}

Export-ModuleMember -Function Invoke-Diagnostics, Measure-HealthScore
