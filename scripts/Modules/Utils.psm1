function Load-Config {
    $path = "..\\config\\config.json"
    return Get-Content $path | ConvertFrom-Json
}

function Log {
    param($File, $Message)
    $Message | Out-File $File -Append
}

function Popup {
    param($Message, $Title, $EnablePopups)
    if ($EnablePopups) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, $Title)
    }
}

Export-ModuleMember -Function Load-Config, Log, Popup