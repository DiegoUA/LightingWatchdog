param([switch]$Watchdog)

Import-Module "..\\scripts\\Modules\\Utils.psm1"
Import-Module "..\\scripts\\Modules\\Diagnostics.psm1"
Import-Module "..\\scripts\\Modules\\Watchdog.psm1"
Import-Module "..\\scripts\\Modules\\Trends.psm1"

$Config = Get-Config

if ($Watchdog) {
    Start-Watchdog -Config $Config
} else {
    Invoke-Diagnostics -Config $Config
}