using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Azure.Communication.CallAutomation;
using AccountRecoveryApi.Infrastructure;

namespace AccountRecoveryApi.Domain;

public enum SessionState { AwaitingId, AwaitingCode, CodeVerified, LinkIssued, Completed, Escalated }

public class RecoverySession
{
    public string SessionId { get; set; } = null!;
    public string? AccountId { get; set; }
    public bool IsRealAccount { get; set; } = false; // Anti-enumeration security shield
    public string CallConnectionId { get; set; } = null!;
    public string? VerificationCode { get; set; }
    public int CodeAttempts { get; set; }
    public DateTime CodeExpiry { get; set; }
    public string? ResetToken { get; set; }
    public DateTime LinkExpiry { get; set; }
    public SessionState State { get; set; }
    public string? ResolutionReceipt { get; set; }
}

public class MockInboxFixture
{
    public string? LastSentCode { get; set; }
    public string? SecureResetLink { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ResetResult
{
    public bool Success { get; set; }
    public string? ReceiptId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
}

public interface IRecoverySessionEngine
{
    void CreateSession(string callConnectionId);
    RecoverySession? GetByCallId(string callConnectionId);
    RecoverySession? GetByResetToken(string token);
    MockInboxFixture? GetMockInbox(string accountId);
    void InvalidateSession(string sessionId);
    Task ProcessCallInputAsync(string sessionId, string input, CallConnection connection);
    Task<ResetResult> ExecuteAuthoritativeResetAsync(string token, string rawPassword);
}

public class InMemoryRecoveryEngine : IRecoverySessionEngine
{
    private readonly IMemoryCache _cache;

    private readonly IRecoveryMonitorQueue _queueAccess;
    private const string SessionPrefix = "rec_sess_";
    private const string CallPrefix = "call_idx_";
    private const string TokenPrefix = "tok_idx_";
    private const string InboxPrefix = "inbox_";

    public InMemoryRecoveryEngine(IMemoryCache cache, IRecoveryMonitorQueue queueAccess) {
        _cache = cache;
        _queueAccess = queueAccess;
    }

    public void CreateSession(string callConnectionId)
    {
        var sessionId = Guid.NewGuid().ToString();
        var session = new RecoverySession { SessionId = sessionId, CallConnectionId = callConnectionId, State = SessionState.AwaitingId };
        _cache.Set($"{SessionPrefix}{sessionId}", session, TimeSpan.FromMinutes(15));
        _cache.Set($"{CallPrefix}{callConnectionId}", sessionId, TimeSpan.FromMinutes(15));
    }

    public RecoverySession? GetByCallId(string callConnectionId) =>
        _cache.TryGetValue($"{CallPrefix}{callConnectionId}", out string? sid) ? _cache.Get<RecoverySession>($"{SessionPrefix}{sid}") : null;

    public RecoverySession? GetByResetToken(string token) =>
        _cache.TryGetValue($"{TokenPrefix}{token}", out string? sid) ? _cache.Get<RecoverySession>($"{SessionPrefix}{sid}") : null;

    public MockInboxFixture? GetMockInbox(string accountId) => _cache.Get<MockInboxFixture>($"{InboxPrefix}{accountId}");

    public void InvalidateSession(string sessionId)
    {
        if (_cache.TryGetValue($"{SessionPrefix}{sessionId}", out RecoverySession? session))
        {
            _cache.Remove($"{SessionPrefix}{sessionId}");
            _cache.Remove($"{CallPrefix}{session!.CallConnectionId}");
            if (session.ResetToken != null) _cache.Remove($"{TokenPrefix}{session.ResetToken}");
        }
    }

    public async Task ProcessCallInputAsync(string sessionId, string input, CallConnection connection)
    {
    var session = _cache.Get<RecoverySession>($"{SessionPrefix}{sessionId}");
    if (session == null || session.State == SessionState.Escalated || session.State == SessionState.Completed) return;

    // PHASE 1: Account Identification Input
    if (session.State == SessionState.AwaitingId)
    {
        session.AccountId = input;
        session.IsRealAccount = _cache.TryGetValue($"synthetic_user_{input}", out _);
        
        session.VerificationCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        session.CodeExpiry = DateTime.UtcNow.AddSeconds(120);
        session.State = SessionState.AwaitingCode;

        if (session.IsRealAccount)
        {
            var mockInbox = new MockInboxFixture { LastSentCode = session.VerificationCode, Timestamp = DateTime.UtcNow };
            _cache.Set($"{InboxPrefix}{session.AccountId}", mockInbox, TimeSpan.FromMinutes(12));
        }

        _cache.Set($"{SessionPrefix}{sessionId}", session, TimeSpan.FromMinutes(15));

        // Fix: Null-guard the telephony media layout for offline mock payloads
        if (connection != null)
        {
            var media = connection.GetCallMedia();
            await media.PlayToAllAsync(new TextSource("A verification token has been routed to your registered security inbox channel. Please speak or enter that six-digit code now."));
        }
        return;
    }

    // PHASE 2: Challenge Code Input Processing
    if (session.State == SessionState.AwaitingCode)
    {
        if (DateTime.UtcNow > session.CodeExpiry)
        {
            await HandleEscalationAsync(connection, session, "Verification challenge token window expired.");
            return;
        }

        if (!session.IsRealAccount || session.VerificationCode != input)
        {
            session.CodeAttempts++;
            if (session.CodeAttempts >= 2)
            {
                await HandleEscalationAsync(connection, session, "Maximum sequence validation failures achieved.");
                return;
            }

            _cache.Set($"{SessionPrefix}{sessionId}", session);

            // Fix: Null-guard the telephony media layout for offline mock payloads
            if (connection != null)
            {
                var media = connection.GetCallMedia();
                await media.PlayToAllAsync(new TextSource("Invalid credentials entry recorded. Please repeat your security token parameter code."));
            }
            return;
        }

        // Phase 3: Successful Verification -> Link Generation
        session.State = SessionState.CodeVerified;
        session.ResetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        session.LinkExpiry = DateTime.UtcNow.AddMinutes(10);
        session.State = SessionState.LinkIssued;

        var currentInbox = _cache.Get<MockInboxFixture>($"{InboxPrefix}{session.AccountId}") ?? new MockInboxFixture();
        currentInbox.SecureResetLink = $"/reset.html?token={session.ResetToken}";
        _cache.Set($"{InboxPrefix}{session.AccountId}", currentInbox, TimeSpan.FromMinutes(10));
        _cache.Set($"{TokenPrefix}{session.ResetToken}", session.SessionId, TimeSpan.FromMinutes(10));
        _cache.Set($"{SessionPrefix}{sessionId}", session, TimeSpan.FromMinutes(15));

        // Fix: Null-guard the telephony media layout for offline mock payloads
        if (connection != null)
        {
            var media = connection.GetCallMedia();
            await media.PlayToAllAsync(new TextSource("Identity confirmed. An isolated profile recovery interface link has reached your secure desktop environment workspace. Please process your password alteration privately."));
            _queueAccess.QueueSessionForMonitoring(sessionId);
        }
    }
}


    public async Task<ResetResult> ExecuteAuthoritativeResetAsync(string token, string rawPassword)
    {
        var session = GetByResetToken(token);
        if (session == null || DateTime.UtcNow > session.LinkExpiry || session.State != SessionState.LinkIssued)
        {
            return new ResetResult { Success = false, ErrorCode = "ERR_LINK_INVALID", ErrorDescription = "Token connection invalid or replayed." };
        }

        // Programmatic syntax filter isolation logic
        if (rawPassword.Length < 8)
        {
            return new ResetResult { Success = false, ErrorCode = "ERR_PASSWORD_WEAK", ErrorDescription = "Complexity policy constraint unmatched." };
        }

        var receipt = $"REC-TKT-{RandomNumberGenerator.GetInt32(100000, 999999)}";
        session.ResolutionReceipt = receipt;
        session.State = SessionState.Completed;
        
        _cache.Set($"{SessionPrefix}{session.SessionId}", session);
        return new ResetResult { Success = true, ReceiptId = receipt };
    }

private async Task HandleEscalationAsync(CallConnection connection, RecoverySession session, string message)
{
    session.State = SessionState.Escalated;
    session.ResolutionReceipt = $"ESC-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}";
    _cache.Set($"{SessionPrefix}{session.SessionId}", session,TimeSpan.FromMinutes(15));
    
    _cache.Remove($"{CallPrefix}{session.CallConnectionId}");
    if (!string.IsNullOrEmpty(session.ResetToken))
    {
        _cache.Remove($"{TokenPrefix}{session.ResetToken}");
    }
    
    
    Console.WriteLine($"[MOCK IT TICKET LOG] System Escalation Triggered for Account {session.AccountId ?? "UNKNOWN"}. Reason: {message}");
    
    if (connection != null)
    {
        var media = connection.GetCallMedia();
        await media.PlayToAllAsync(new TextSource("Verification protocols exhausted. Transferring your active session footprint tracking parameters over to operational service desk agents."));
    }
}

}