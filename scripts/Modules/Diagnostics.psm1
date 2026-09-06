function Measure-HealthScore {
    param($Result, $Config)

    $score = 100
    $w = $Config.Weights

    # Check if ANY monitored service is currently leaking
    $isLeaking = $false
    if ($Result.ServiceStates) {
        foreach ($s in $Result.ServiceStates) {
            if ($s.LeakDetected) { $isLeaking = $true }
        }
    }

    if ($isLeaking) { $score -= $w.ServiceLeak }
    if ($Result.NonPagedPressure)     { $score -= $w.NonPagedPressure }
    if ($Result.StormDetected)        { $score -= $w.Storm }
    if ($Result.TcpTotal -gt $Config.LeakThreshold) { $score -= $w.TcpLoad }
    if ($Result.UdpNewPerSec -gt $Config.StormThreshold) { $score -= $w.UdpLoad }

    return [math]::Min(100, [math]::Max(0, $score))
}

function Get-LeakGrowthRate {
    param([string]$DiagnosticsCsvPath, [int]$Window)

    if (!(Test-Path $DiagnosticsCsvPath)) { return 0 }

    $data = Import-Csv $DiagnosticsCsvPath | Select-Object -Last $Window
    if ($data.Count -lt 2) { return 0 }

    $first = $data[0]
    $last  = $data[-1]

    try {
        $firstTime = [datetime]::Parse($first.Timestamp)
        $lastTime  = [datetime]::Parse($last.Timestamp)
    } catch { return 0 }

    $deltaConns = [double]$last.MonitoredConnsSum - [double]$first.MonitoredConnsSum
    $deltaSeconds = ($lastTime - $firstTime).TotalSeconds

    if ($deltaSeconds -le 0) { return 0 }

    return [math]::Round($deltaConns / $deltaSeconds, 2)
}

function Invoke-Diagnostics {
    param($Config)

    # --- Absolute folders ---
    $logFolder    = Join-Path $PSScriptRoot "..\..\logs"
    $exportFolder = Join-Path $logFolder "export"

    if (!(Test-Path $logFolder))    { New-Item -ItemType Directory -Path $logFolder | Out-Null }
    if (!(Test-Path $exportFolder)) { New-Item -ItemType Directory -Path $exportFolder | Out-Null }

    # --- Timestamp ---
    $timestamp = Get-Timestamp -UseUtc $Config.UseUtc -TimestampFormat $Config.TimestampFormat

    # --- Log file ---
    $logFile = Join-Path $logFolder "NetworkDiag_$timestamp.txt"
    "=== DIAGNOSTICS ($timestamp) ===`n" | Out-File $logFile -Encoding UTF8

    # --- Result object ---
    $result = [ordered]@{
        Timestamp          = $timestamp
        TcpTotal           = $null
        ServiceStates      = @()
        MonitoredConnsSum  = 0
        NonPagedPercent    = $null
        NonPagedPressure   = $false
        TcpNewPerSec       = $null
        UdpNewPerSec       = $null
        StormDetected      = $false
        HealthScore        = $null
        RollingAverage     = $null
        RollingStdDev      = $null
        ZScore             = $null
        TrendDirection     = $null
        LeakGrowthRate     = $null
        NonPagedTrend      = $null
    }

    # --- TCP count ---
    $tcpTotal = (Get-NetTCPConnection -ErrorAction SilentlyContinue | Measure-Object).Count
    $result.TcpTotal = $tcpTotal
    Write-Log -File $logFile -Message "TCP connections: $tcpTotal"

    # --- Abstracted Service Leak Detection ---
    $serviceStates = @()
    $totalMonitoredConns = 0

    foreach ($target in $Config.MonitoredServices) {
        $connCount = 0
        $procs = Get-Process -Name $target.ProcessTree -ErrorAction SilentlyContinue
        if ($procs) {
            foreach ($p in $procs) {
                $conns = Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue
                if ($conns) { $connCount += $conns.Count }
            }
        }
        
        $isLeaking = ($connCount -gt $target.MaxTcpConnections)
        $totalMonitoredConns += $connCount

        Write-Log -File $logFile -Message "$($target.ServiceName) connections: $connCount"
        
        if ($isLeaking) {
            Write-Log -File $logFile -Message "LEAK DETECTED in $($target.ServiceName)"
            Show-Alert "$($target.ServiceName) leak detected ($connCount conns)" "Watchdog" $Config.EnablePopups
        }

        $serviceStates += @{
            ServiceName        = $target.ServiceName
            ProcessTree        = $target.ProcessTree
            CurrentConnections = $connCount
            LeakDetected       = $isLeaking
            EnableRestart      = $target.EnableRestart
        }
    }

    $result.ServiceStates = $serviceStates
    $result.MonitoredConnsSum = $totalMonitoredConns

    # --- Nonpaged pool ---
    $os    = Get-CimInstance Win32_OperatingSystem
    $np    = [double]$os.NonPagedPoolSize
    $npMax = [double]$os.NonPagedPoolQuota

    if (-not $npMax -or $npMax -le 0 -or $npMax -lt $np) {
        Write-Log -File $logFile -Message "WARNING: NonPagedPoolQuota invalid. Using fallback."
        $npMax = [math]::Max($np, 1) * 2
    }

    $npPct = [math]::Round(($np / $npMax) * 100, 2)
    $result.NonPagedPercent = $npPct
    Write-Log -File $logFile -Message "Nonpaged pool: $npPct%"

    if ($npPct -gt $Config.NonPagedPoolThreshold) {
        $result.NonPagedPressure = $true
        Show-Alert "Nonpaged pool high ($npPct%)" "Kernel Alert" $Config.EnablePopups
        Write-Log -File $logFile -Message "KERNEL PRESSURE DETECTED"
    }

    # --- WebSocket storm ---
    $tcp1 = (Get-NetTCPConnection -ErrorAction SilentlyContinue | Measure-Object).Count
    $udp1 = (Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Measure-Object).Count

    Start-Sleep -Seconds 1

    $tcp2 = (Get-NetTCPConnection -ErrorAction SilentlyContinue | Measure-Object).Count
    $udp2 = (Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Measure-Object).Count

    $result.TcpNewPerSec = $tcp2 - $tcp1
    $result.UdpNewPerSec = $udp2 - $udp1

    Write-Log -File $logFile -Message "TCP/sec: $($result.TcpNewPerSec)"
    Write-Log -File $logFile -Message "UDP/sec: $($result.UdpNewPerSec)"

    if ($result.TcpNewPerSec -gt $Config.StormThreshold -or
        $result.UdpNewPerSec -gt $Config.StormThreshold) {

        $result.StormDetected = $true
        Show-Alert "WebSocket storm detected" "Storm Alert" $Config.EnablePopups
        Write-Log -File $logFile -Message "STORM DETECTED"
    }

    # --- Health score ---
    $result.HealthScore = Measure-HealthScore -Result $result -Config $Config
    Write-Log -File $logFile -Message "Health Score: $($result.HealthScore)"

    # --- Trend analysis ---
    $trendCsvPath       = Join-Path $exportFolder "HealthTrend_v2.csv"
    $diagnosticsCsvPath = Join-Path $exportFolder "diagnostics_v2.csv"

    $trendData = Get-TrendData -CsvPath $trendCsvPath -Window $Config.TrendWindow
    $trend     = Measure-Trend -CurrentScore $result.HealthScore -TrendData $trendData

    $result.RollingAverage = $trend.RollingAverage
    $result.RollingStdDev  = $trend.RollingStdDev
    $result.ZScore         = $trend.ZScore
    $result.TrendDirection = $trend.TrendDirection

    Write-Log -File $logFile -Message "Trend: Avg=$($trend.RollingAverage), StdDev=$($trend.RollingStdDev), Z=$($trend.ZScore), Dir=$($trend.TrendDirection)"

    # --- Leak growth rate ---
    $result.LeakGrowthRate = Get-LeakGrowthRate -DiagnosticsCsvPath $diagnosticsCsvPath -Window $Config.TrendWindow
    Write-Log -File $logFile -Message "Leak growth rate: $($result.LeakGrowthRate) connections/sec"

    # --- Nonpaged trend ---
    if ($null -ne $trendData -and $trendData.Count -ge 3) {
        $npValues = $trendData.NonPagedPercent | ForEach-Object { [double]$_ }
        $npAvg = ($npValues | Measure-Object -Average).Average
        $npDelta = $result.NonPagedPercent - $npAvg

        $result.NonPagedTrend =
            if ($npDelta -gt 5) { "Degrading" }
            elseif ($npDelta -lt -5) { "Improving" }
            else { "Stable" }
    } else {
        $result.NonPagedTrend = "Unknown"
    }

    Write-Log -File $logFile -Message "Nonpaged trend: $($result.NonPagedTrend)"

    # --- Predictive alerts ---
    if ($Config.EnablePredictiveAlerts) {
        if ([math]::Abs($trend.ZScore) -ge 2) {
            Write-Log -File $logFile -Message "ANOMALY: Health score deviates significantly (Z=$($trend.ZScore))."
            Show-Alert "Health anomaly detected (Z=$($trend.ZScore)). Trend: $($trend.TrendDirection)." "LightingWatchdog Trend" $Config.EnablePopups
        }

        if ($result.LeakGrowthRate -gt 10) {
            Write-Log -File $logFile -Message "PREDICTIVE: Leak growth rate high ($($result.LeakGrowthRate) connections/sec)."
        }
    }

    # --- JSON export ---
    if ($Config.EnableJsonExport) {
        $jsonPath = Join-Path $exportFolder "diag_$timestamp.json"
        $result | ConvertTo-Json -Depth 4 | Out-File $jsonPath -Encoding UTF8
    }

    # --- CSV export ---
    if ($Config.EnableCsvExport) {
        # Create a simplified object for the CSV to avoid complex nested arrays
        $csvResult = [ordered]@{
            Timestamp         = $result.Timestamp
            TcpTotal          = $result.TcpTotal
            MonitoredConnsSum = $result.MonitoredConnsSum
            NonPagedPercent   = $result.NonPagedPercent
            TcpNewPerSec      = $result.TcpNewPerSec
            UdpNewPerSec      = $result.UdpNewPerSec
            HealthScore       = $result.HealthScore
            LeakGrowthRate    = $result.LeakGrowthRate
        }
        $obj = New-Object PSObject -Property $csvResult

        if (!(Test-Path $diagnosticsCsvPath)) {
            $obj | Export-Csv -Path $diagnosticsCsvPath -NoTypeInformation
        } else {
            $obj | Export-Csv -Path $diagnosticsCsvPath -NoTypeInformation -Append
        }
    }

    # --- HealthTrend.csv export ---
    $trendRow = New-Object PSObject -Property @{
        Timestamp      = $result.Timestamp
        HealthScore    = $result.HealthScore
        RollingAverage = $result.RollingAverage
        RollingStdDev  = $result.RollingStdDev
        ZScore         = $result.ZScore
        TrendDirection = $result.TrendDirection
        NonPagedPercent= $result.NonPagedPercent
        NonPagedTrend  = $result.NonPagedTrend
    }

    if (!(Test-Path $trendCsvPath)) {
        $trendRow | Export-Csv -Path $trendCsvPath -NoTypeInformation
    } else {
        $trendRow | Export-Csv -Path $trendCsvPath -NoTypeInformation -Append
    }

    return $logFile
}

Export-ModuleMember -Function Get-LeakGrowthRate, Invoke-Diagnostics, Measure-HealthScore