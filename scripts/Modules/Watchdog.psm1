Import-Module "..\\scripts\\Modules\\Utils.psm1"
Import-Module "..\\scripts\\Modules\\Diagnostics.psm1"

$global:LastRestartTimestamp = $null

function Write-RestartEvent {
    param(
        [string]$Reason,
        $Result,
        $Config
    )

    $exportFolder = "..\\logs\\export"
    if (!(Test-Path $exportFolder)) {
        New-Item -ItemType Directory -Path $exportFolder | Out-Null
    }

    $restartCsvPath = "$exportFolder\\RestartEvents.csv"

    $row = New-Object PSObject -Property @{
        Timestamp            = $Result.Timestamp
        Reason               = $Reason
        HealthScore          = $Result.HealthScore
        TcpTotal             = $Result.TcpTotal
        LightingServiceConns = $Result.LightingServiceConns
        NonPagedPercent      = $Result.NonPagedPercent
        TcpNewPerSec         = $Result.TcpNewPerSec
        UdpNewPerSec         = $Result.UdpNewPerSec
        LeakGrowthRate       = $Result.LeakGrowthRate
    }

    if (!(Test-Path $restartCsvPath)) {
        $row | Export-Csv -Path $restartCsvPath -NoTypeInformation
    } else {
        $row | Export-Csv -Path $restartCsvPath -NoTypeInformation -Append
    }

    if ($Config.EnableWebhooks -and $Result.HealthScore -lt $Config.WebhookMinHealthScore) {
        $payload = @{
            Timestamp      = $Result.Timestamp
            Event          = "LightingServiceRestart"
            Reason         = $Reason
            HealthScore    = $Result.HealthScore
            TcpTotal       = $Result.TcpTotal
            LightingConns  = $Result.LightingServiceConns
            NonPagedPercent= $Result.NonPagedPercent
            LeakGrowthRate = $Result.LeakGrowthRate
        }
        Send-WebhookNotification -WebhookUrl $Config.WebhookUrl -Payload $payload -EnableWebhooks $Config.EnableWebhooks
    }
}

function Start-Watchdog {
    param(
        $Config
    )

    Show-Alert "LightingWatchdog started in continuous mode." "Watchdog" $Config.EnablePopups

    while ($true) {

        # Run diagnostics
        $logFile = Invoke-Diagnostics -Config $Config

        # Load latest JSON
        $exportFolder = "..\\logs\\export"
        $latestJson = Get-ChildItem $exportFolder -Filter "diag_*.json" | Sort-Object Name -Descending | Select-Object -First 1

        if ($latestJson) {
            $data = Get-Content $latestJson.FullName | ConvertFrom-Json

            # v2.4.1 timestamp fix
            try {
                $dataTime = [datetime]::ParseExact($data.Timestamp, "yyyy-MM-dd_HH-mm-ss", $null)
            } catch {
                $dataTime = Get-Date
            }

            $needRestart = $false
            $reason = $null

            if ($data.LightingLeakDetected) {
                $needRestart = $true
                $reason = "LightingLeak"
            } elseif ($data.NonPagedPressure) {
                $needRestart = $true
                $reason = "KernelPressure"
            } elseif ($data.StormDetected) {
                $needRestart = $true
                $reason = "Storm"
            }

            if ($needRestart) {

                # Cooldown logic
                $now = Get-Date
                if ($global:LastRestartTimestamp -ne $null) {
                    $elapsed = ($now - $global:LastRestartTimestamp).TotalSeconds
                    if ($elapsed -lt $Config.CooldownSeconds) {
                        Write-Log $logFile "Cooldown active ($elapsed s < $($Config.CooldownSeconds) s). Skipping restart."
                        $needRestart = $false
                    }
                }

                if ($needRestart) {
                    Write-Log $logFile "Restarting LightingService due to $reason."

                    try {
                        $proc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
                        if ($proc) {
                            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                        }

                        Start-Sleep -Seconds 2
                        Start-Service -Name LightingService -ErrorAction Stop

                        Write-Log $logFile "LightingService restarted successfully."
                        $global:LastRestartTimestamp = Get-Date

                        Write-RestartEvent -Reason $reason -Result $data -Config $Config

                    } catch {
                        Write-Log $logFile "Failed to restart LightingService: $_"
                    }
                }
            }
        }

        Start-Sleep -Seconds $Config.WatchdogIntervalSeconds
    }
}

Export-ModuleMember -Function Start-Watchdog