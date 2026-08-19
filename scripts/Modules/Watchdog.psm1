Import-Module (Join-Path $PSScriptRoot "Utils.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "Diagnostics.psm1") -Force

$global:LastRestartTimestamp = $null
$global:RestartHistory = @()

function Write-RestartEvent {
    param(
        [string]$Reason,
        $Result,
        $Config
    )

    $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
    if (!(Test-Path $exportFolder)) {
        New-Item -ItemType Directory -Path $exportFolder | Out-Null
    }

    $restartCsvPath = Join-Path $exportFolder "RestartEvents.csv"

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
            Timestamp       = $Result.Timestamp
            Event           = "LightingServiceRestart"
            Reason          = $Reason
            HealthScore     = $Result.HealthScore
            TcpTotal        = $Result.TcpTotal
            LightingConns   = $Result.LightingServiceConns
            NonPagedPercent = $Result.NonPagedPercent
            LeakGrowthRate  = $Result.LeakGrowthRate
        }
        Send-WebhookNotification -WebhookUrl $Config.WebhookUrl -Payload $payload -EnableWebhooks $Config.EnableWebhooks
    }
}

function Update-Heartbeat {
    param(
        [datetime]$LastHeartbeat,
        [datetime]$LastRestart,
        [int]$WatchdogHealth,
        [int]$CycleTimeSeconds
    )

    $logFolder = Join-Path $PSScriptRoot "..\..\logs"
    if (!(Test-Path $logFolder)) {
        New-Item -ItemType Directory -Path $logFolder | Out-Null
    }

    $heartbeatPath = Join-Path $logFolder "heartbeat.json"

    $obj = @{
        LastHeartbeat    = $LastHeartbeat.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ssZ")
        LastRestart      = $LastRestart.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ssZ")
        WatchdogHealth   = $WatchdogHealth
        CycleTimeSeconds = $CycleTimeSeconds
    }

    $obj | ConvertTo-Json -Depth 4 | Out-File $heartbeatPath -Encoding UTF8
}

function Check-Quarantine {
    param(
        [datetime]$Now,
        $Config
    )

    if (-not $Config.EnableQuarantine) {
        return $false
    }

    $windowStart = $Now.AddMinutes(-$Config.QuarantineWindowMinutes)
    $global:RestartHistory = $global:RestartHistory | Where-Object { $_ -ge $windowStart }

    if ($global:RestartHistory.Count -ge $Config.QuarantineRestartLimit) {
        return $true
    }

    return $false
}

function Apply-AutoKill {
    param(
        $Config,
        [string]$LogFile
    )

    if (-not $Config.EnableAutoKill) {
        return
    }

    foreach ($name in $Config.AutoKillProcesses) {
        $proc = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($proc) {
            $memMB = [math]::Round($proc.WorkingSet64 / 1MB, 2)

            if ($memMB -gt $Config.AutoKillMemThresholdMB) {
                Write-Log -File $LogFile -Message "AUTO-KILL: $name using $memMB MB. Terminating."
                try {
                    Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                } catch {
                    Write-Log -File $LogFile -Message "AUTO-KILL FAILED: $name - $_"
                }
            }
        }
    }
}

function Start-Watchdog {
    param(
        $Config
    )

    Show-Alert "LightingWatchdog started in continuous mode." "Watchdog" $Config.EnablePopups

    $lastHeartbeat  = Get-Date
    $lastRestart    = Get-Date
    $watchdogHealth = 100

    while ($true) {

        $cycleStart = Get-Date

        $logFile = Invoke-Diagnostics -Config $Config

        Apply-AutoKill -Config $Config -LogFile $logFile

        $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
        $latestJson = Get-ChildItem $exportFolder -Filter "diag_*.json" -ErrorAction SilentlyContinue |
                      Sort-Object Name -Descending | Select-Object -First 1

        if ($latestJson) {
            $data = Get-Content $latestJson.FullName | ConvertFrom-Json

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

            $now = Get-Date

            $inQuarantine = Check-Quarantine -Now $now -Config $Config
            if ($inQuarantine) {
                Write-Log -File $logFile -Message "QUARANTINE ACTIVE: Skipping restart of LightingService."
                $needRestart = $false
            }

            if ($needRestart) {

                if ($global:LastRestartTimestamp -ne $null) {
                    $elapsed = ($now - $global:LastRestartTimestamp).TotalSeconds
                    if ($elapsed -lt $Config.CooldownSeconds) {
                        Write-Log -File $logFile -Message "Cooldown active ($elapsed s < $($Config.CooldownSeconds) s). Skipping restart."
                        $needRestart = $false
                    }
                }

                if ($needRestart) {
                    Write-Log -File $logFile -Message "Restarting LightingService due to $reason."

                    try {
                        $proc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
                        if ($proc) {
                            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                        }

                        Start-Sleep -Seconds 2
                        Start-Service -Name LightingService -ErrorAction Stop

                        Write-Log -File $logFile -Message "LightingService restarted successfully."
                        $global:LastRestartTimestamp = Get-Date
                        $lastRestart = $global:LastRestartTimestamp
                        $global:RestartHistory += $lastRestart

                        Write-RestartEvent -Reason $reason -Result $data -Config $Config

                    } catch {
                        Write-Log -File $logFile -Message "Failed to restart LightingService: $_"
                    }
                }
            }
        }

        $cycleEnd = Get-Date
        $cycleDuration = ($cycleEnd - $cycleStart).TotalSeconds
        $drift = $cycleDuration - $Config.WatchdogIntervalSeconds

        if ([math]::Abs($drift) -gt $Config.ClockDriftThresholdSeconds) {
            Write-Log -File $logFile -Message "CLOCK DRIFT: Cycle duration $cycleDuration s (expected $($Config.WatchdogIntervalSeconds) s)."
            $watchdogHealth = [math]::Max(0, $watchdogHealth - 5)
        } else {
            $watchdogHealth = [math]::Min(100, $watchdogHealth + 1)
        }

        $lastHeartbeat = Get-Date
        Update-Heartbeat -LastHeartbeat $lastHeartbeat -LastRestart $lastRestart -WatchdogHealth $watchdogHealth -CycleTimeSeconds $Config.WatchdogIntervalSeconds

        Start-Sleep -Seconds $Config.WatchdogIntervalSeconds
    }
}

Export-ModuleMember -Function Start-Watchdog