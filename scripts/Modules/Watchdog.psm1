$global:LastRestartTimestamp = $null
$global:RestartHistory = @()

function Write-RestartEvent {
    param(
        [string]$Reason,
        [string]$ServiceName,
        [int]$CurrentConns,
        $Result,
        $Config
    )

    $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
    if (!(Test-Path $exportFolder)) {
        New-Item -ItemType Directory -Path $exportFolder | Out-Null
    }

    $restartCsvPath = Join-Path $exportFolder "RestartEvents_v2.csv"

    $row = New-Object PSObject -Property @{
        Timestamp       = $Result.Timestamp
        Reason          = $Reason
        ServiceName     = $ServiceName
        ServiceConns    = $CurrentConns
        HealthScore     = $Result.HealthScore
        NonPagedPercent = $Result.NonPagedPercent
        LeakGrowthRate  = $Result.LeakGrowthRate
    }

    if (!(Test-Path $restartCsvPath)) {
        $row | Export-Csv -Path $restartCsvPath -NoTypeInformation
    } else {
        $row | Export-Csv -Path $restartCsvPath -NoTypeInformation -Append
    }

    if ($Config.EnableWebhooks -and $Result.HealthScore -lt $Config.WebhookMinHealthScore) {
        $payload = @{
            Timestamp   = $Result.Timestamp
            Event       = "ServiceRestart"
            Reason      = $Reason
            Target      = $ServiceName
            Connections = $CurrentConns
            HealthScore = $Result.HealthScore
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

    if (-not $Config.EnableQuarantine) { return $false }

    $windowStart = $Now.AddMinutes(-$Config.QuarantineWindowMinutes)
    $global:RestartHistory = @($global:RestartHistory | Where-Object { $_ -ge $windowStart })

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

    if (-not $Config.EnableAutoKill) { return }

    foreach ($name in $Config.AutoKillProcesses) {
        $proc = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($proc) {
            $memMB = [math]::Round($proc.WorkingSet64 / 1MB, 2)
            if ($memMB -gt $Config.AutoKillMemThresholdMB) {
                Write-Log -File $LogFile -Message "AUTO-KILL: $name using $memMB MB. Terminating."
                try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } 
                catch { Write-Log -File $LogFile -Message "AUTO-KILL FAILED: $name - $_" }
            }
        }
    }
}

function Start-Watchdog {
    param($Config)

    Write-Host "Watchdog started in continuous mode. Running autonomously..." -ForegroundColor Cyan

    $lastHeartbeat  = Get-Date
    $lastRestart    = Get-Date
    $watchdogHealth = 100

    while ($true) {
        $cycleStart = Get-Date

        # Run diagnostics
        $diagOutput = Invoke-Diagnostics -Config $Config

        # Safe Log File Extraction
        $logFile = $null
        if ($diagOutput -is [array]) {
            $validPaths = $diagOutput | Where-Object { $_ -is [string] -and $_ -match '\.log$' }
            if ($validPaths) { $logFile = $validPaths[-1] }
        } elseif ($diagOutput -is [string] -and $diagOutput -match '\.log$') {
            $logFile = $diagOutput
        }

        if ([string]::IsNullOrWhiteSpace($logFile)) {
            $fallbackDir = Join-Path $PSScriptRoot "..\..\logs"
            if (!(Test-Path $fallbackDir)) { New-Item -ItemType Directory -Path $fallbackDir | Out-Null }
            $logFile = Join-Path $fallbackDir "watchdog_fallback.log"
        }
        $logFile = [string]$logFile

        Invoke-AutoKill -Config $Config -LogFile $logFile

        # Load Diagnostics JSON
        $exportFolder = Join-Path $PSScriptRoot "..\..\logs\export"
        $latestJson = Get-ChildItem $exportFolder -Filter "diag_*.json" -ErrorAction SilentlyContinue |
                      Sort-Object Name -Descending | Select-Object -First 1

        if ($latestJson) {
            $data = Get-Content $latestJson.FullName | ConvertFrom-Json
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Scan complete." -ForegroundColor DarkGray

            $now = Get-Date

            # Evaluate each monitored service dynamically
            if ($data.ServiceStates) {
                foreach ($service in $data.ServiceStates) {
                    Write-Host " -> $($service.ServiceName): $($service.CurrentConnections) connections" -ForegroundColor DarkGray
                    
                    if ($service.LeakDetected -and $service.EnableRestart) {
                        
                        $inQuarantine = Test-Quarantine -Now $now -Config $Config
                        if ($inQuarantine) {
                            Write-Log -File $logFile -Message "QUARANTINE ACTIVE: Skipping restart for $($service.ServiceName)."
                            Write-Host "--> RESTART BLOCKED: Quarantine is active." -ForegroundColor DarkRed
                            continue
                        }

                        if ($null -ne $global:LastRestartTimestamp) {
                            $elapsed = ($now - $global:LastRestartTimestamp).TotalSeconds
                            if ($elapsed -lt $Config.CooldownSeconds) {
                                Write-Log -File $logFile -Message "Cooldown active ($elapsed s). Skipping restart for $($service.ServiceName)."
                                Write-Host "--> RESTART BLOCKED: Cooldown active ($([math]::Round($elapsed)) sec)." -ForegroundColor DarkYellow
                                continue
                            }
                        }

                        # Dynamic Aggressive Restart Logic
                        Write-Host ""
                        Write-Host ">>> LEAK DETECTED: $($service.ServiceName) has $($service.CurrentConnections) connections. <<<" -ForegroundColor Red
                        Write-Log -File $logFile -Message "Restarting $($service.ServiceName) due to TCP Leak."

                        try {
                            Write-Host "Attempting graceful stop of $($service.ServiceName)..." -ForegroundColor Yellow
                            Stop-Service -Name $service.ServiceName -Force -ErrorAction SilentlyContinue | Out-Null
                            
                            $rogueProcesses = Get-Process -Name $service.ProcessTree -ErrorAction SilentlyContinue
                            if ($rogueProcesses) {
                                foreach ($proc in $rogueProcesses) {
                                    Write-Host "Force killing process tree for PID $($proc.Id) ($($proc.Name))..." -ForegroundColor DarkYellow
                                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue | Out-Null
                                    taskkill /F /T /PID $proc.Id 2>&1 | Out-Null 
                                }
                            }

                            $verifyWait = 0
                            while ((Get-Process -Name $service.ProcessTree -ErrorAction SilentlyContinue) -and ($verifyWait -lt 10)) {
                                Start-Sleep -Seconds 1
                                $verifyWait++
                            }

                            if (Get-Process -Name $service.ProcessTree -ErrorAction SilentlyContinue) {
                                Write-Host "CRITICAL ERROR: Could not kill $($service.ServiceName)! Run as Administrator." -ForegroundColor Red
                            } else {
                                Write-Host "Process dead. Waiting 15s for Windows kernel to flush sockets..." -ForegroundColor Yellow
                                $flushWaitSeconds = 15
                                while ($flushWaitSeconds -gt 0) {
                                    Write-Host -NoNewline "."
                                    Start-Sleep -Seconds 1
                                    $flushWaitSeconds--
                                }
                                Write-Host ""
                            }

                            Write-Host "Sockets flushed. Restarting $($service.ServiceName)..." -ForegroundColor Cyan
                            Start-Service -Name $service.ServiceName -ErrorAction SilentlyContinue
                            
                            Start-Sleep -Seconds 3
                            $newConns = 0
                            $newProc = Get-Process -Name $service.ProcessTree -ErrorAction SilentlyContinue
                            if ($newProc) {
                                foreach ($p in $newProc) {
                                    $conns = Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue
                                    if ($conns) { $newConns += $conns.Count }
                                }
                            }
                            
                            Write-Host "Service restarted. Current connections: $newConns" -ForegroundColor Green
                            Write-Host ""

                            Write-Log -File $logFile -Message "$($service.ServiceName) restored. Connections: $newConns"
                            $global:LastRestartTimestamp = Get-Date
                            $lastRestart = $global:LastRestartTimestamp
                            $global:RestartHistory += $lastRestart

                            Write-RestartEvent -Reason "$($service.ServiceName) Leak" -ServiceName $service.ServiceName -CurrentConns $newConns -Result $data -Config $Config

                        } catch {
                            Write-Host "ERROR: Failed to restart $($service.ServiceName): $_" -ForegroundColor Red
                            Write-Log -File $logFile -Message "Failed to restart $($service.ServiceName): $_"
                        }
                    }
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