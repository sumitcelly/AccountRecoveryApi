param(
    [string]$BaseUrl = "https://localhost:7208"
)

$Username = $env:MOCK_INBOX_USERNAME
$InboxPassword = $env:MOCK_INBOX_PASSWORD
if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($InboxPassword)) {
    throw "Set MOCK_INBOX_USERNAME and MOCK_INBOX_PASSWORD before running this script."
}

$WebSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$LoginBody = @{
    username = $Username
    password = $InboxPassword
} | ConvertTo-Json

try {
    Invoke-RestMethod -Uri "$BaseUrl/api/mock/login" -Method Post `
        -ContentType "application/json" -Body $LoginBody -WebSession $WebSession | Out-Null
    $Inbox = Invoke-RestMethod -Uri "$BaseUrl/api/mock/inbox" -Method Get -WebSession $WebSession
} catch {
    throw "Inbox sign-in failed. Check the credentials and service URL."
}

if ($Inbox.verificationCode) {
    Write-Host "Current synthetic verification code: $($Inbox.verificationCode)" -ForegroundColor Cyan
}
if ([string]::IsNullOrWhiteSpace($Inbox.resetLink)) {
    Write-Host "No reset link is available yet. Complete account and verification-code steps in the live call, then rerun this script."
    return
}

$ResetUri = [Uri]::new([Uri]::new("$BaseUrl/"), $Inbox.resetLink)
$Token = [System.Web.HttpUtility]::ParseQueryString($ResetUri.Query).Get("token")
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "The authenticated inbox did not contain a valid reset link."
}

$SecurePassword = Read-Host "Enter a new synthetic password (minimum 8 characters)" -AsSecureString
$NewPassword = [PSCredential]::new("unused", $SecurePassword).GetNetworkCredential().Password
$ResetBody = @{
    token = $Token
    newPassword = $NewPassword
} | ConvertTo-Json

try {
    $Receipt = Invoke-RestMethod -Uri "$BaseUrl/api/recovery/reset-password" -Method Post `
        -ContentType "application/json" -Body $ResetBody
    $Replay = Invoke-RestMethod -Uri "$BaseUrl/api/recovery/reset-password" -Method Post `
        -ContentType "application/json" -Body $ResetBody
    if ($Replay.receipt -ne $Receipt.receipt) {
        throw "The reset retry did not return the original authoritative receipt."
    }

    Write-Host "Synthetic password reset completed. Receipt: $($Receipt.receipt)" -ForegroundColor Green
    Write-Host "Idempotent retry returned the same receipt." -ForegroundColor Green
} finally {
    $NewPassword = $null
    $ResetBody = $null
    $Token = $null
}

Invoke-RestMethod -Uri "$BaseUrl/api/mock/logout" -Method Post -WebSession $WebSession | Out-Null
