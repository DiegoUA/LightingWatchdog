Import-Module "..\\scripts\\Modules\\Utils.psm1"
Import-Module "..\\scripts\\Modules\\Diagnostics.psm1"

function Start-Watchdog {
    param(
        $Config
    )

    Show-Alert "LightingWatchdog started in continuous mode." "Watchdog" $Config.EnablePopups

    while ($true) {
        $log = Invoke-Diagnostics -Config $Config
        Start-Sleep -Seconds $Config.WatchdogIntervalSeconds
    }
}

Export-ModuleMember -Function Start-Watchdog