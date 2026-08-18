param([switch]$Watchdog)

Import-Module "$PSScriptRoot\Modules\Utils.psm1"
Import-Module "$PSScriptRoot\Modules\Diagnostics.psm1"
Import-Module "$PSScriptRoot\Modules\Watchdog.psm1"
Import-Module "$PSScriptRoot\Modules\Trends.psm1"

$Config = Get-Config

if ($Watchdog) {
    Start-Watchdog -Config $Config
} else {
    Invoke-Diagnostics -Config $Config
}