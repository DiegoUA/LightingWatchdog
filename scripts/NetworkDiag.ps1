param([switch]$Watchdog)

Import-Module "..\\scripts\\Modules\\Utils.psm1"
Import-Module "..\\scripts\\Modules\\Diagnostics.psm1"
Import-Module "..\\scripts\\Modules\\Watchdog.psm1"

$Config = Load-Config

if ($Watchdog) {
    Start-Watchdog -Config $Config
} else {
    Run-Diagnostics -Config $Config
}