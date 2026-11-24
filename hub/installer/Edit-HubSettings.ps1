<#  Bert's IPTV Hub - edit settings and restart the hub.
    Installed to the hub folder and linked from the Start menu ("Edit hub settings"). #>

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$Host.UI.RawUI.WindowTitle = "Bert's IPTV Hub - settings"
$settings = Join-Path $PSScriptRoot 'appsettings.json'

while ($true) {
    Write-Host "Opening $settings - save and close Notepad when you're done." -ForegroundColor Cyan
    Start-Process notepad.exe -ArgumentList "`"$settings`"" -Wait
    try {
        Get-Content $settings -Raw | ConvertFrom-Json | Out-Null
        break
    } catch {
        Write-Host "That file is not valid JSON anymore (check commas and quotes): $($_.Exception.Message)" -ForegroundColor Red
        $again = Read-Host "Open it again to fix? (Y/n)"
        if ($again -match '^[nN]') { Write-Host "Not restarting the hub."; Start-Sleep 3; exit 1 }
    }
}

Write-Host "Restarting the hub..." -ForegroundColor Cyan
Restart-Service BertsIptvHub
Start-Sleep -Seconds 2
Get-Service BertsIptvHub | Format-Table -AutoSize DisplayName, Status
Start-Sleep -Seconds 3
