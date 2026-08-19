param([switch]$Watchdog)

# Resolve module directory absolutely
$ModuleDir = Join-Path $PSScriptRoot "Modules"

Import-Module (Join-Path $ModuleDir "Utils.psm1") -Force
Import-Module (Join-Path $ModuleDir "Diagnostics.psm1") -Force
Import-Module (Join-Path $ModuleDir "Watchdog.psm1") -Force
Import-Module (Join-Path $ModuleDir "Trends.psm1") -Force

# Load config.json using Utils.psm1 (already path-safe)
$Config = Get-Config

if ($Watchdog) {
    Start-Watchdog -Config $Config
} else {
    Invoke-Diagnostics -Config $Config
}