#Requires -RunAsAdministrator

    $projectName = "NetworkWatchdogService"

    Write-Host "Stopping $projectName..." -ForegroundColor Cyan
    sc.exe stop $projectName
    Start-Sleep -Seconds 2

    Write-Host "Deleting $projectName..." -ForegroundColor Cyan
    sc.exe delete $projectName

    Write-Host "Uninstallation Complete!" -ForegroundColor Green