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
    Write-Host "LightingWatchdog started in continuous mode. Monitoring..." -ForegroundColor Cyan

    $lastHeartbeat  = Get-Date
    $lastRestart    = Get-Date
    $watchdogHealth = 100

    while ($true) {
        $cycleStart = Get-Date

        # Run diagnostics
        $diagOutput = Invoke-Diagnostics -Config $Config

        # Extract log file
        $logFile = $null
        if ($diagOutput -is [array]) {
            $validPaths = $diagOutput | Where-Object { $_ -is [string] -and $_ -match '\.log$' }
            if ($validPaths) { $logFile = $validPaths[-1] }
        } elseif ($diagOutput -is [string] -and $diagOutput -match '\.log$') {
            $logFile = $diagOutput
        }

        # Fallback log
        if ([string]::IsNullOrWhiteSpace($logFile)) {
            $fallbackDir = Join-Path $PSScriptRoot "..\..\logs"
            if (!(Test-Path $fallbackDir)) { New-Item -ItemType Directory -Path $fallbackDir | Out-Null }
            $logFile = Join-Path $fallbackDir "watchdog_fallback.log"
        }
        $logFile = [string]$logFile

        Invoke-AutoKill -Config $Config -LogFile $logFile

        # Load latest diagnostics JSON
        $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
        $latestJson = Get-ChildItem $exportFolder -Filter "diag_*.json" -ErrorAction SilentlyContinue |
                      Sort-Object Name -Descending | Select-Object -First 1

        if ($latestJson) {
            $data = Get-Content $latestJson.FullName | ConvertFrom-Json

            # Live Console Feedback for the cycle
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Scan complete. LightingService TCP: $($data.LightingServiceConns)" -ForegroundColor DarkGray

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

            # Quarantine check
            $inQuarantine = Test-Quarantine -Now $now -Config $Config
            if ($inQuarantine -and $needRestart) {
                Write-Log -File $logFile -Message "QUARANTINE ACTIVE: Skipping restart of LightingService."
                Write-Host "--> RESTART BLOCKED: Quarantine is active." -ForegroundColor DarkRed
                $needRestart = $false
            }

            # Cooldown check
            if ($needRestart) {
                if ($null -ne $global:LastRestartTimestamp) {
                    $elapsed = ($now - $global:LastRestartTimestamp).TotalSeconds
                    if ($elapsed -lt $Config.CooldownSeconds) {
                        Write-Log -File $logFile -Message "Cooldown active ($elapsed s < $($Config.CooldownSeconds) s). Skipping restart."
                        Write-Host "--> RESTART BLOCKED: Cooldown active ($elapsed sec)." -ForegroundColor DarkYellow
                        $needRestart = $false
                    }
                }
            }

            # Restart logic containing aggressive shutdown, console feedback, and re-evaluation
            if ($needRestart) {
                Write-Host ""
                Write-Host ">>> LEAK DETECTED: LightingService currently has $($data.LightingServiceConns) connections. <<<" -ForegroundColor Red
                Write-Log -File $logFile -Message "Restarting LightingService due to $reason."

                try {
                    Write-Host "Attempting to stop LightingService..." -ForegroundColor Yellow
                    Stop-Service -Name LightingService -Force -ErrorAction SilentlyContinue | Out-Null
                    
                    $rogueProcesses = Get-Process -Name "LightingService", "AuraService" -ErrorAction SilentlyContinue
                    if ($rogueProcesses) {
                        foreach ($proc in $rogueProcesses) {
                            Write-Host "Force killing process tree for PID $($proc.Id)..." -ForegroundColor DarkYellow
                            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue | Out-Null
                            taskkill /F /T /PID $proc.Id 2>&1 | Out-Null 
                        }
                    }

                    $verifyWait = 0
                    while ((Get-Process -Name "LightingService" -ErrorAction SilentlyContinue) -and ($verifyWait -lt 10)) {
                        Start-Sleep -Seconds 1
                        $verifyWait++
                    }

                    if (Get-Process -Name "LightingService" -ErrorAction SilentlyContinue) {
                        Write-Host "CRITICAL ERROR: Could not kill LightingService! Ensure script is running as Administrator." -ForegroundColor Red
                    } else {
                        Write-Host "Service stopped. Waiting 15 seconds for Windows kernel to flush TCP sockets..." -ForegroundColor Yellow
                        $flushWaitSeconds = 15
                        while ($flushWaitSeconds -gt 0) {
                            Write-Host -NoNewline "."
                            Start-Sleep -Seconds 1
                            $flushWaitSeconds--
                        }
                        Write-Host ""
                    }

                    Write-Host "TCP sockets flushed. Restarting service..." -ForegroundColor Cyan
                    Start-Service -Name LightingService -ErrorAction SilentlyContinue
                    
                    # Wait for service to boot and re-evaluate connections
                    Start-Sleep -Seconds 3
                    $newProc = Get-Process -Name LightingService -ErrorAction SilentlyContinue
                    $newConns = 0
                    if ($newProc) {
                        $newConns = (Get-NetTCPConnection -OwningProcess $newProc.Id -ErrorAction SilentlyContinue | Measure-Object).Count
                    }
                    
                    Write-Host "Service restarted. Re-evaluation complete. Current connections: $newConns" -ForegroundColor Green
                    Write-Host ""

                    Write-Log -File $logFile -Message "LightingService restarted successfully. New connection count: $newConns"
                    $global:LastRestartTimestamp = Get-Date
                    $lastRestart = $global:LastRestartTimestamp
                    $global:RestartHistory += $lastRestart

                    Write-RestartEvent -Reason $reason -Result $data -Config $Config

                } catch {
                    Write-Host "ERROR: Failed to restart LightingService: $_" -ForegroundColor Red
                    Write-Log -File $logFile -Message "Failed to restart LightingService: $_"
                }
            }
        }

        $lastHeartbeat = Get-Date
        Update-Heartbeat -LastHeartbeat $lastHeartbeat -LastRestart $lastRestart -WatchdogHealth $watchdogHealth -CycleTimeSeconds $Config.WatchdogIntervalSeconds

        Start-Sleep -Seconds $Config.WatchdogIntervalSeconds

        $cycleEnd = Get-Date
        $cycleDuration = ($cycleEnd - $cycleStart).TotalSeconds
        $drift = $cycleDuration - $Config.WatchdogIntervalSeconds

        if ([math]::Abs($drift) -gt $Config.ClockDriftThresholdSeconds) {
            $watchdogHealth = [math]::Max(0, $watchdogHealth - 5)
        } else {
            $watchdogHealth = [math]::Min(100, $watchdogHealth + 1)
        }
    }
}

Export-ModuleMember -Function Start-Watchdog