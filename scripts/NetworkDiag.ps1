param([switch]$Watchdog)

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

if ($Watchdog) {
    Start-Watchdog -Config $Config
} else {
    Invoke-Diagnostics -Config $Config
}