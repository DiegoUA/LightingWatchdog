function Get-TrendData {
    param(
        [string]$CsvPath,
        [int]$Window
    )

    if (!(Test-Path $CsvPath)) {
        return $null
    }

    $data = Import-Csv $CsvPath | Select-Object -Last $Window
    return $data
}

function Measure-Trend {
    param(
        [double]$CurrentScore,
        $TrendData
    )

    if ($TrendData -eq $null -or $TrendData.Count -lt 3) {
        return @{
            RollingAverage = $CurrentScore
            RollingStdDev  = 0
            ZScore         = 0
            TrendDirection = "Unknown"
        }
    }

    $scores = $TrendData.HealthScore | ForEach-Object { [double]$_ }

    # Manual stddev for PS 5.1
    $avg = ($scores | Measure-Object -Average).Average
    $variance = ($scores | ForEach-Object { ($_ - $avg) * ($_ - $avg) } |
                 Measure-Object -Sum).Sum / $scores.Count
    $std = [math]::Sqrt($variance)

    if ($std -eq 0) { $std = 0.0001 }

    $z = ($CurrentScore - $avg) / $std

    $trend =
        if ($CurrentScore -gt $avg) { "Improving" }
        elseif ($CurrentScore -lt $avg) { "Degrading" }
        else { "Stable" }

    return @{
        RollingAverage = [math]::Round($avg, 2)
        RollingStdDev  = [math]::Round($std, 2)
        ZScore         = [math]::Round($z, 2)
        TrendDirection = $trend
    }
}

Export-ModuleMember -Function Get-TrendData, Measure-Trend