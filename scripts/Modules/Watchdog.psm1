$global:LastRestartTimestamp = $null   # last SUCCESSFUL restart
$global:LastRestartAttempt = $null     # last restart attempt, successful or not (drives the cooldown)
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

# Names that auto-kill refuses to touch, whatever the configuration says.
$script:AutoKillProtected = @('idle','system','registry','smss','csrss','wininit','winlogon','services','lsass','svchost','dwm','explorer','powershell','pwsh')
$script:AutoKillWarned    = @{}   # one-time warnings (bad config entries, unkillable PIDs)

function Invoke-AutoKill {
    param(
        $Config,
        [string]$LogFile
    )

    if (-not $Config.EnableAutoKill) { return }

    # Memory limit per process, in MB. A missing or invalid value disables auto-kill (warned once).
    $limitMB = 0.0
    try { $limitMB = [double]$Config.AutoKillMemThresholdMB } catch { $limitMB = 0.0 }
    if ($limitMB -le 0) {
        if (-not $script:AutoKillWarned.ContainsKey('limit')) {
            $script:AutoKillWarned['limit'] = $true
            Write-Host "AUTO-KILL DISABLED: AutoKillMemThresholdMB is missing or not a positive number." -ForegroundColor Yellow
            Write-Log -File $LogFile -Message "AUTO-KILL DISABLED: AutoKillMemThresholdMB is missing or not a positive number."
        }
        return
    }

    $isAdmin = Test-IsAdministrator

    foreach ($entry in @($Config.AutoKillProcesses)) {
        if ([string]::IsNullOrWhiteSpace([string]$entry)) { continue }

        # Accept "Name" and "Name.exe"
        $name = ([string]$entry).Trim() -replace '\.exe$', ''

        # Exact names only: a wildcard such as "*" would otherwise match every process.
        if ($name -match '[\*\?\[\]]') {
            if (-not $script:AutoKillWarned.ContainsKey("cfg:$name")) {
                $script:AutoKillWarned["cfg:$name"] = $true
                Write-Host "AUTO-KILL: ignoring '$name' (wildcards are not allowed)." -ForegroundColor Yellow
                Write-Log -File $LogFile -Message "AUTO-KILL: ignoring '$name' (wildcards are not allowed)."
            }
            continue
        }
        if ($script:AutoKillProtected -contains $name.ToLowerInvariant()) {
            if (-not $script:AutoKillWarned.ContainsKey("cfg:$name")) {
                $script:AutoKillWarned["cfg:$name"] = $true
                Write-Host "AUTO-KILL: ignoring '$name' (protected system process)." -ForegroundColor Yellow
                Write-Log -File $LogFile -Message "AUTO-KILL: ignoring '$name' (protected system process)."
            }
            continue
        }

        # Every instance is judged on its own: @() makes zero, one or many instances behave the same.
        foreach ($proc in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            if ($proc.Id -eq $PID) { continue }

            try { $memMB = [math]::Round($proc.WorkingSet64 / 1MB, 2) } catch { continue }   # exited meanwhile or not readable
            if ($memMB -le $limitMB) { continue }
            if ($script:AutoKillWarned.ContainsKey("pid:$($proc.Id)")) { continue }           # already reported as unkillable

            $msg = "AUTO-KILL: $name (PID $($proc.Id)) uses $memMB MB, limit $limitMB MB. Terminating."
            Write-Host $msg -ForegroundColor DarkYellow
            Write-Log -File $LogFile -Message $msg

            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction Stop
            } catch {
                # Already gone is success; anything else is reported below after the check.
                $stopError = $_.Exception.Message
            }

            $waited = 0
            while ((Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) -and $waited -lt 10) {
                Start-Sleep -Milliseconds 500
                $waited++
            }

            if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
                $script:AutoKillWarned["pid:$($proc.Id)"] = $true
                if ($isAdmin) { $hint = "The process could not be terminated." } else { $hint = "Run PowerShell as Administrator." }
                Write-Host "AUTO-KILL FAILED: $name (PID $($proc.Id)) is still running. $hint" -ForegroundColor Red
                Write-Log -File $LogFile -Message "AUTO-KILL FAILED: $name (PID $($proc.Id)) is still running. $hint $stopError"
            } else {
                Write-Host "AUTO-KILL OK: $name (PID $($proc.Id)) terminated." -ForegroundColor Green
                Write-Log -File $LogFile -Message "AUTO-KILL OK: $name (PID $($proc.Id)) terminated."
            }
            $stopError = $null
        }
    }
}

function Start-Watchdog {
    param(
        $Config,
        [switch]$MonitorOnly   # detect, report and log only: never stop, kill or restart anything
    )

    Write-Host "Watchdog started in continuous mode. Running autonomously..." -ForegroundColor Cyan

    Write-Host "Tip: clicking inside this window pauses it. Press Enter or Esc to resume." -ForegroundColor DarkGray

    $monitorFlagged = @{}      # services already reported as leaking in monitor-only mode

    if ($MonitorOnly) {
        Write-Host "MONITOR-ONLY mode: leaks are detected, reported and logged. No process is stopped, killed or restarted." -ForegroundColor Cyan
    } elseif (-not (Test-IsAdministrator)) {
        Write-Host "WARNING: PowerShell is not running as Administrator. Diagnostics and leak detection work, but restarting services and auto-kill will fail. Restart PowerShell with 'Run as administrator'." -ForegroundColor Yellow
    }

    $lastHeartbeat  = Get-Date
    $lastRestart    = Get-Date
    $watchdogHealth = 100

    while ($true) {
        $cycleStart = Get-Date

        # Run diagnostics (the result of this cycle is published in $global:LastDiagnosticsResult)
        $global:LastDiagnosticsResult = $null
        $diagOutput = Invoke-Diagnostics -Config $Config

        # Safe Log File Extraction
        $logFile = $null
        if ($diagOutput -is [array]) {
            $validPaths = $diagOutput | Where-Object { $_ -is [string] -and $_ -match '\.(log|txt)$' }
            if ($validPaths) { $logFile = $validPaths[-1] }
        } elseif ($diagOutput -is [string] -and $diagOutput -match '\.(log|txt)$') {
            $logFile = $diagOutput
        }

        if ([string]::IsNullOrWhiteSpace($logFile)) {
            $fallbackDir = Join-Path $PSScriptRoot "..\..\logs"
            if (!(Test-Path $fallbackDir)) { New-Item -ItemType Directory -Path $fallbackDir | Out-Null }
            $logFile = Join-Path $fallbackDir "watchdog_fallback.log"
        }
        $logFile = [string]$logFile

        if (-not $MonitorOnly) {
            Invoke-AutoKill -Config $Config -LogFile $logFile
        }

        # Use this cycle's result from memory: works with EnableJsonExport on or off and never acts on stale data
        $data = $global:LastDiagnosticsResult

        if ($null -ne $data) {
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Scan complete." -ForegroundColor DarkGray

            $now = Get-Date

            # Evaluate each monitored service dynamically
            if ($data.ServiceStates) {
                foreach ($service in $data.ServiceStates) {
                    Write-Host " -> $($service.ServiceName): $($service.CurrentConnections) connections" -ForegroundColor DarkGray

                    if ($MonitorOnly) {
                        # Report each leak episode once, then stay quiet until the service is healthy again
                        if ($service.LeakDetected) {
                            if (-not $monitorFlagged.ContainsKey($service.ServiceName)) {
                                $monitorFlagged[$service.ServiceName] = $true
                                Write-Host ">>> LEAK DETECTED: $($service.ServiceName) has $($service.CurrentConnections) connections. Monitor-only mode: no action taken. <<<" -ForegroundColor Red
                                Write-Log -File $logFile -Message "LEAK DETECTED: $($service.ServiceName) has $($service.CurrentConnections) connections. Monitor-only mode: no action taken."
                            }
                        } else {
                            $monitorFlagged.Remove($service.ServiceName)
                        }
                        continue
                    }
                    
                    if ($service.LeakDetected -and $service.EnableRestart) {
                        
                        $inQuarantine = Test-Quarantine -Now $now -Config $Config
                        if ($inQuarantine) {
                            Write-Log -File $logFile -Message "QUARANTINE ACTIVE: Skipping restart for $($service.ServiceName)."
                            Write-Host "--> RESTART BLOCKED: Quarantine is active." -ForegroundColor DarkRed
                            continue
                        }

                        if ($null -ne $global:LastRestartAttempt) {
                            $elapsed = ($now - $global:LastRestartAttempt).TotalSeconds
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
                                # The process survived: do NOT report a restart, do NOT count it.
                                $global:LastRestartAttempt = Get-Date
                                if (Test-IsAdministrator) {
                                    $killHint = "The process could not be terminated."
                                } else {
                                    $killHint = "Run PowerShell as Administrator."
                                }
                                Write-Host "RESTART FAILED: Could not kill $($service.ServiceName)! $killHint" -ForegroundColor Red
                                Write-Log -File $logFile -Message "RESTART FAILED: could not kill $($service.ServiceName). $killHint No restart was performed."
                                continue
                            }

                            Write-Host "Process dead. Waiting 15s for Windows kernel to flush sockets..." -ForegroundColor Yellow
                            $flushWaitSeconds = 15
                            while ($flushWaitSeconds -gt 0) {
                                Write-Host -NoNewline "."
                                Start-Sleep -Seconds 1
                                $flushWaitSeconds--
                            }
                            Write-Host ""

                            Write-Host "Sockets flushed. Restarting $($service.ServiceName)..." -ForegroundColor Cyan
                            Start-Service -Name $service.ServiceName -ErrorAction SilentlyContinue

                            $startWait = 0
                            $svcCheck = Get-Service -Name $service.ServiceName -ErrorAction SilentlyContinue
                            while ($svcCheck -and $svcCheck.Status -ne 'Running' -and $startWait -lt 10) {
                                Start-Sleep -Seconds 1
                                $startWait++
                                $svcCheck.Refresh()
                            }

                            if (-not $svcCheck -or $svcCheck.Status -ne 'Running') {
                                $global:LastRestartAttempt = Get-Date
                                Write-Host "RESTART FAILED: $($service.ServiceName) is not running after the start attempt." -ForegroundColor Red
                                Write-Log -File $logFile -Message "RESTART FAILED: $($service.ServiceName) did not reach the Running state."
                                continue
                            }

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
                            $global:LastRestartAttempt = $global:LastRestartTimestamp
                            $lastRestart = $global:LastRestartTimestamp
                            $global:RestartHistory += $lastRestart

                            Write-RestartEvent -Reason "$($service.ServiceName) Leak" -ServiceName $service.ServiceName -CurrentConns $newConns -Result $data -Config $Config

                        } catch {
                            $global:LastRestartAttempt = Get-Date
                            Write-Host "ERROR: Failed to restart $($service.ServiceName): $_" -ForegroundColor Red
                            Write-Log -File $logFile -Message "Failed to restart $($service.ServiceName): $_"
                        }
                    }
                }
            }
        } else {
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Diagnostics returned no result this cycle; nothing evaluated." -ForegroundColor DarkYellow
            Write-Log -File $logFile -Message "Diagnostics returned no result this cycle; nothing evaluated."
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