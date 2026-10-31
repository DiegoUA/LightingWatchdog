param([switch]$Watchdog, [switch]$MonitorOnly)

# --- Resolve absolute script directory ---
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir
$ModuleDir = Join-Path $ScriptDir "Modules"

Write-Host "ScriptDir = $ScriptDir"
Write-Host "ModuleDir = $ModuleDir"

# --- Import modules in logical dependency order ---
$modules = @("Utils.psm1", "Trends.psm1", "Diagnostics.psm1", "Watchdog.psm1")

foreach ($m in $modules) {
    $path = Join-Path $ModuleDir $m
    Write-Host "Importing $path"
    Import-Module $path -Scope Global -Force -ErrorAction Stop
}

# --- Verify loaded functions ---
Write-Host "Loaded functions:"
Get-Command Get-Config, Invoke-Diagnostics, Start-Watchdog | Format-Table Name, Module

# --- Main execution ---
$Config = Get-Config

if ($null -eq $Config) {
    Write-Host "ERROR: config\config.json is missing or is not valid JSON. See logs\application.log. Nothing was started." -ForegroundColor Red
    exit 1
}

if ($MonitorOnly -and -not $Watchdog) {
    Write-Host "NOTE: -MonitorOnly only applies together with -Watchdog. Running a single diagnostic scan." -ForegroundColor Yellow
}

if ($Watchdog) {
    Start-Watchdog -Config $Config -MonitorOnly:$MonitorOnly
} else {
    Invoke-Diagnostics -Config $Config
}