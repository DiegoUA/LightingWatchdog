#Requires -RunAsAdministrator

    $projectName = "NetworkWatchdogService"
    $projectPath = Join-Path$PSScriptRoot "..\src\NetworkWatchdogService\NetworkWatchdogService.csproj"
    $publishDir = Join-Path$PSScriptRoot "..\bin\Release"

    Write-Host "Publishing $projectName to$publishDir..." -ForegroundColor Cyan
    dotnet publish $projectPath -c Release -o$publishDir

    $exePath = Join-Path $publishDir "$projectName.exe"

    if (-Not (Test-Path $exePath)) {
        Write-Error "Failed to locate published executable at $exePath."
        exit 1
    }

    Write-Host "Stopping existing service (if any)..." -ForegroundColor Cyan
    sc.exe stop $projectName | Out-Null
    Start-Sleep -Seconds 2

    Write-Host "Creating Windows Service..." -ForegroundColor Cyan
    sc.exe create $projectName binPath= "$exePath" start= auto DisplayName= "Network Watchdog Service"

    Write-Host "Setting Service Description..." -ForegroundColor Cyan
    sc.exe description $projectName "Monitors and mitigates TCP socket leaks caused by LightingService."

    Write-Host "Configuring Failure Recovery Options..." -ForegroundColor Cyan
    # Restart the service on the 1st, 2nd, and 3rd failures after 60s. Reset fail count after 1 day.
    sc.exe failure $projectName reset= 86400 actions= restart/60000/restart/60000/restart/60000

    Write-Host "Starting Service..." -ForegroundColor Cyan
    sc.exe start $projectName

    Write-Host "Installation Complete! Service is now running autonomously." -ForegroundColor Green