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

function Send-WebhookNotification {
    param(
        [string]$WebhookUrl,
        [hashtable]$Payload,
        [bool]$EnableWebhooks
    )

    if (-not $EnableWebhooks -or [string]::IsNullOrWhiteSpace($WebhookUrl)) {
        return
    }

    try {
        $json = $Payload | ConvertTo-Json -Depth 4
        Invoke-RestMethod -Uri $WebhookUrl -Method Post -Body $json -ContentType "application/json"
    } catch {
        # Best-effort only; failures are ignored
    }
}

Export-ModuleMember -Function Get-Config, Write-Log, Show-Alert, Send-WebhookNotification