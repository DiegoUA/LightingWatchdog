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

function Test-Quarantine {
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

function Invoke-AutoKill {
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

        # --- FIX: cycleStart BEFORE everything ---
        $cycleStart = Get-Date

        # Run diagnostics and capture all output
        $diagOutput = Invoke-Diagnostics -Config $Config

        # Safely extract the log file path without assuming the array isn't null
        $logFile = $null
        if ($diagOutput -is [array]) {
            $validPaths = $diagOutput | Where-Object { $_ -is [string] -and $_ -match '\.log$' }
            if ($validPaths) {
                $logFile = $validPaths[-1]
            }
        } elseif ($diagOutput -is [string] -and $diagOutput -match '\.log$') {
            $logFile = $diagOutput
        }

        # FAILSAFE: If Invoke-Diagnostics failed to return a valid path, use a fallback so the watchdog doesn't crash
        if ([string]::IsNullOrWhiteSpace($logFile)) {
            $fallbackDir = Join-Path $PSScriptRoot "..\..\logs"
            if (!(Test-Path $fallbackDir)) {
                New-Item -ItemType Directory -Path $fallbackDir | Out-Null
            }
            $logFile = Join-Path $fallbackDir "watchdog_fallback.log"
        }

        # Failsafe cast to ensure Write-Log receives a strict string
        $logFile = [string]$logFile

        # AutoKill
        Invoke-AutoKill -Config $Config -LogFile $logFile

        # Load latest diagnostics JSON
        $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
        $latestJson = Get-ChildItem $exportFolder -Filter "diag_*.json" -ErrorAction SilentlyContinue |
                      Sort-Object Name -Descending | Select-Object -First 1

        if ($latestJson) {
            $data = Get-Content $latestJson.FullName | ConvertFrom-Json

            $needRestart = $false
            $reason = $null

            # Restore the condition checks to set the flags
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

            # Quarantine check
            $inQuarantine = Test-Quarantine -Now $now -Config $Config
            if ($inQuarantine) {
                Write-Log -File $logFile -Message "QUARANTINE ACTIVE: Skipping restart of LightingService."
                $needRestart = $false
            }

            # Cooldown check
            if ($needRestart) {
                if ($null -ne $global:LastRestartTimestamp) {
                    $elapsed = ($now - $global:LastRestartTimestamp).TotalSeconds
                    if ($elapsed -lt $Config.CooldownSeconds) {
                        Write-Log -File $logFile -Message "Cooldown active ($elapsed s < $($Config.CooldownSeconds) s). Skipping restart."
                        $needRestart = $false
                    }
                }
            }

            # Restart logic containing the aggressive shutdown, death verification, and TCP flush
            if ($needRestart) {
                Write-Log -File $logFile -Message "Restarting LightingService due to $reason."

                try {
                    Write-Log -File $logFile -Message "LEAK DETECTED: Initiating aggressive shutdown of LightingService..."
                    
                    # 1. Attempt graceful stop first to satisfy SCM
                    Stop-Service -Name LightingService -Force -ErrorAction SilentlyContinue | Out-Null
                    
                    # 2. Kill the process tree (LightingService + orphaned children holding sockets)
                    $rogueProcesses = Get-Process -Name "LightingService", "AuraService" -ErrorAction SilentlyContinue
                    if ($rogueProcesses) {
                        foreach ($proc in $rogueProcesses) {
                            Write-Log -File $logFile -Message "Force killing process tree for PID $($proc.Id)..."
                            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue | Out-Null
                            taskkill /F /T /PID $proc.Id 2>&1 | Out-Null 
                        }
                    }

                    # 3. VERIFY process is actually dead
                    $verifyWait = 0
                    while ((Get-Process -Name "LightingService" -ErrorAction SilentlyContinue) -and ($verifyWait -lt 10)) {
                        Write-Log -File $logFile -Message "Waiting for LightingService process to die..."
                        Start-Sleep -Seconds 1
                        $verifyWait++
                    }

                    if (Get-Process -Name "LightingService" -ErrorAction SilentlyContinue) {
                        Write-Log -File $logFile -Message "CRITICAL ERROR: Could not kill LightingService! Ensure this script is running as Administrator."
                    } else {
                        # 4. The TCP Flush Cooldown Loop
                        Write-Log -File $logFile -Message "Process dead. Waiting 15s for Windows kernel to flush TIME_WAIT sockets..."
                        $flushWaitSeconds = 15
                        while ($flushWaitSeconds -gt 0) {
                            Start-Sleep -Seconds 1
                            $flushWaitSeconds--
                        }
                    }

                    # 5. Safe Restart
                    Write-Log -File $logFile -Message "TCP sockets flushed. Restarting LightingService..."
                    Start-Service -Name LightingService -ErrorAction SilentlyContinue

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

        # Heartbeat update
        $lastHeartbeat = Get-Date
        Update-Heartbeat -LastHeartbeat $lastHeartbeat -LastRestart $lastRestart -WatchdogHealth $watchdogHealth -CycleTimeSeconds $Config.WatchdogIntervalSeconds

        # Sleep for interval
        Start-Sleep -Seconds $Config.WatchdogIntervalSeconds

        # --- FIX: measure cycle AFTER sleep ---
        $cycleEnd = Get-Date
        $cycleDuration = ($cycleEnd - $cycleStart).TotalSeconds
        $drift = $cycleDuration - $Config.WatchdogIntervalSeconds

        if ([math]::Abs($drift) -gt $Config.ClockDriftThresholdSeconds) {
            Write-Log -File $logFile -Message "CLOCK DRIFT: Cycle duration $cycleDuration s (expected $($Config.WatchdogIntervalSeconds) s)."
            $watchdogHealth = [math]::Max(0, $watchdogHealth - 5)
        } else {
            $watchdogHealth = [math]::Min(100, $watchdogHealth + 1)
        }
    }
}

Export-ModuleMember -Function Start-Watchdog