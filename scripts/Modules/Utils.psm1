function Get-Config {
    $path = "..\\config\\config.json"
    return Get-Content $path | ConvertFrom-Json
}

function Write-Log {
    param(
        [string]$File,
        [string]$Message
    )
    $Message | Out-File $File -Append
}

function Show-Alert {
    param(
        [string]$Message,
        [string]$Title,
        [bool]$EnablePopups
    )
    if ($EnablePopups) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, $Title)
    }
}

Export-ModuleMember -Function Get-Config, Write-Log, Show-Alert