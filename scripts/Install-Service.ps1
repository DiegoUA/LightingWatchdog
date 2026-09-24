#Requires -RunAsAdministrator

$projectName = "NetworkWatchdogService"

Write-Host "Stopping existing service to unlock files..." -ForegroundColor Cyan
sc.exe stop $projectName | Out-Null
Start-Sleep -Seconds 3

# Corrected parameter spacing and path joins
$projectPath = Join-Path -Path $PSScriptRoot -ChildPath "..\src\NetworkWatchdogService\NetworkWatchdogService.csproj"
$publishDir  = Join-Path -Path $PSScriptRoot -ChildPath "..\bin\Release"

Write-Host "Publishing $projectName to $publishDir..." -ForegroundColor Cyan
dotnet publish "$projectPath" -c Release -r win-x64 --no-self-contained -o "$publishDir"

$exePath = Join-Path -Path $publishDir -ChildPath "$projectName.exe"

if (-Not (Test-Path $exePath)) {
    Write-Error "Failed to locate published executable at $exePath."
    exit 1
}

Write-Host "Creating Windows Service (will harmlessly fail if it already exists)..." -ForegroundColor Cyan
sc.exe create $projectName binPath= "`"$exePath`"" start= auto DisplayName= "Network Watchdog Service"

Write-Host "Setting Service Description..." -ForegroundColor Cyan
sc.exe description $projectName "Monitors and mitigates TCP socket leaks caused by LightingService."

Write-Host "Configuring Failure Recovery Options..." -ForegroundColor Cyan
sc.exe failure $projectName reset= 86400 actions= restart/60000/restart/60000/restart/60000

Write-Host "Starting Service..." -ForegroundColor Cyan
sc.exe start $projectName

Write-Host "Installation/Update Complete! Service is now running autonomously." -ForegroundColor Green