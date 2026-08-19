function Get-Config {
    # config folder is two levels above Modules\
    $configPath = Join-Path $PSScriptRoot "..\..\config\config.json"

    if (Test-Path $configPath) {
        try {
            return Get-Content $configPath -Raw | ConvertFrom-Json
        } catch {
            Write-Log "Failed to parse config.json: $_"
            return $null
        }
    } else {
        Write-Log "Config file not found at $configPath"
        return $null
    }
}

function Get-Timestamp {
    param(
        [bool]$UseUtc,
        [string]$TimestampFormat
    )

    $now = if ($UseUtc) { (Get-Date).ToUniversalTime() } else { Get-Date }

    switch ($TimestampFormat) {
        "ISO8601" { return $now.ToString("yyyy-MM-ddTHH-mm-ssZ") }
        default   { return $now.ToString("yyyy-MM-dd_HH-mm-ss") }
    }
}

function Write-Log {
    param(
        [Parameter(Position = 0)]
        [string]$Message,

        [Parameter(Position = 1)]
        [string]$File = "application.log"
    )

    # If -File was called named or positionally, handle parameter swapping if needed
    if ([string]::IsNullOrWhiteSpace($Message) -and -not [string]::IsNullOrWhiteSpace($File)) {
        $Message = $File
        $File = "application.log"
    }

    # If caller passes a relative name, resolve it under ..\..\logs
    if (-not [System.IO.Path]::IsPathRooted($File)) {
        $logDir = Join-Path $PSScriptRoot "..\..\logs"

        if (-not (Test-Path $logDir)) {
            New-Item -ItemType Directory -Path $logDir | Out-Null
        }

        $File = Join-Path $logDir $File
    }

    $Message | Out-File -FilePath $File -Append -Encoding UTF8
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
        Write-Log "Webhook send failed: $_"
    }
}

Export-ModuleMember -Function Get-Config, Get-Timestamp, Send-WebhookNotification, Show-Alert, Write-Log