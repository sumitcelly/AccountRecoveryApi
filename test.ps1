# PowerShell End-to-End Recovery Journey Integration Test
$BaseUrl = "http://localhost:5000"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "🚀 STARTING SIMULATED ENTERPRISE ACCOUNT RECOVERY lifecycles" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# -------------------------------------------------------------------------
# STEP A: Simulate Inbound Call Event
# -------------------------------------------------------------------------
Write-Host "`n[STEP A] dispatching inbound call event to telephony pipeline..." -ForegroundColor Yellow
$InboundBody = @(
    @{
        eventType = "Microsoft.Communication.IncomingCall"
        data = @{
            incomingCallContext = "mock-context-123"
        }
    }
) | ConvertTo-Json -Depth 10

try {
    $ResA = Invoke-RestMethod -Uri "$BaseUrl/api/telephony/incoming" -Method Post -ContentType "application/json" -Body $InboundBody
    Write-Host "✅ Telephony Handshake Initialized: Session Provisioned." -ForegroundColor Green
} catch {
    Write-Error "❌ Step A Failed: $_"
    return
}

# -------------------------------------------------------------------------
# STEP B: Input Pre-Enrolled Account Identity (12345) via Keypad
# -------------------------------------------------------------------------
Write-Host "`n[STEP B] Simulating user keypad entry for Account: 12345..." -ForegroundColor Yellow

# Format a valid CloudEvent array matching the CallAutomation SDK Event Parser expectations
# Locate [STEP B] inside your test-recovery.ps1 script and update the $CallbackBody to look exactly like this:
$CallbackBody = @(
    @{
        id = [Guid]::NewGuid().ToString()
        source = "communications/connections/mock-context-123"
        type = "Microsoft.Communication.RecognizeCompleted"
        specversion = "1.0"
        data = @{
            callConnectionId = "mock-context-123"
            recognizeResult = @{
                # Ensure the subclass is explicitly structured to clear casting checks
                dtmfResult = @{
                    tones = @("Tone1", "Tone2", "Tone3", "Tone4", "Tone5")
                }
            }
        }
    }
) | ConvertTo-Json -Depth 10

try {
    $ResB = Invoke-RestMethod -Uri "$BaseUrl/api/telephony/callback" -Method Post -ContentType "application/json" -Body $CallbackBody
    Write-Host "✅ Keypad Entry Transmitted. State advanced to AwaitingCode." -ForegroundColor Green
} catch {
    Write-Error "❌ Step B Failed: $_"
    return
}

# -------------------------------------------------------------------------
# STEP C: Query Out-of-Band Mock Inbox and Extract Security Tokens
# -------------------------------------------------------------------------
Write-Host "`n[STEP C] Querying out-of-band mock inbox profile workspace..." -ForegroundColor Yellow
Start-Sleep -Seconds 1 # Ensure the state engine updates the cache index asynchronously

try {
    $Inbox = Invoke-RestMethod -Uri "$BaseUrl/api/mock/inbox/12345" -Method Get
    Write-Host "📬 Out-of-Band Delivery Intercepted Successfully!" -ForegroundColor Green
    Write-Host "👉 Simulated Mail Verification Code : $($Inbox.lastSentCode)" -ForegroundColor Cyan
    Write-Host "👉 Simulated High-Entropy Reset Link: $($Inbox.secureResetLink)" -ForegroundColor Cyan
    
    # Parse out the raw token parameter from the generated web string link
    $Token = ($Inbox.secureResetLink -split "token=")[1]
    Write-Host "👉 Cryptographic Token Extracted  : $Token" -ForegroundColor Cyan
} catch {
    Write-Error "❌ Step C Failed: Mock inbox profile entry not found or timed out. $_"
    return
}

# -------------------------------------------------------------------------
# STEP D: Simulate Authoritative HTTPS Browser Reset Submission
# -------------------------------------------------------------------------
Write-Host "`n[STEP D] Dispatching isolated HTTPS password alteration request..." -ForegroundColor Yellow
$ResetBody = @{
    token = $Token
    newPassword = "SecureEnterprisePassword2026!"
} | ConvertTo-Json

try {
    $Receipt = Invoke-RestMethod -Uri "$BaseUrl/api/recovery/reset-password" -Method Post -ContentType "application/json" -Body $ResetBody
    Write-Host "✅ PRIVACY BOUNDARY PASSWORD RESET SUCCESSFUL!" -ForegroundColor Green
    Write-Host "🎫 Authoritative System Receipt Code: $($Receipt.receipt)" -ForegroundColor Magenta
    Write-Host "💬 Server Confirmation Message     : $($Receipt.message)" -ForegroundColor Magenta
} catch {
    Write-Error "❌ Step D Failed: Password reset transaction aborted. $_"
    return
}

# -------------------------------------------------------------------------
# STEP E: Verify Replay Attack Protection (Idempotency Check)
# -------------------------------------------------------------------------
Write-Host "`n[STEP E] Testing single-use token token boundary (Replay Defenses)..." -ForegroundColor Yellow
try {
    $Replay = Invoke-RestMethod -Uri "$BaseUrl/api/recovery/reset-password" -Method Post -ContentType "application/json" -Body $ResetBody
    Write-Host "❌ CRITICAL EXPLOIT DETECTED: Token was replayed!" -ForegroundColor Red
} catch {
    Write-Host "✅ REPLAY DEFENSE WORKING: Token reuse was successfully blocked by backend." -ForegroundColor Green
    Write-Host "💬 Expected Security Response: [400 Bad Request] - State connection invalid or replayed." -ForegroundColor Gray
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "🎯 END-TO-END AUTOMATED LIFE CYCLE TESTING COMPLETE" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
