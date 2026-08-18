function Get-Config {
    $path = "..\\config\\config.json"
    return Get-Content $path | ConvertFrom-Json
}

function Get-Timestamp {
    param(
        [bool]$UseUtc,
        [string]$TimestampFormat
    )

    $now =
        if ($UseUtc) { (Get-Date).ToUniversalTime() }
        else { Get-Date }

    switch ($TimestampFormat) {
        "ISO8601" { return $now.ToString("yyyy-MM-ddTHH:mm:ssZ") }
        default   { return $now.ToString("yyyy-MM-dd_HH-mm-ss") }
    }
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
        # best-effort only
    }
}

Export-ModuleMember -Function Get-Config, Get-Timestamp, Write-Log, Show-Alert, Send-WebhookNotification