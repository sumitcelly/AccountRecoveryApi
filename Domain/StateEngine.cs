using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AccountRecoveryApi.Infrastructure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AccountRecoveryApi.Domain;

public enum SessionState { AwaitingId, AwaitingCode, CodeVerified, LinkIssued, Completed, Escalated }

public sealed class RecoverySession
{
    public string SessionId { get; set; } = null!;
    public string? AccountId { get; set; }
    public bool IsRealAccount { get; set; }
    public string? CallConnectionId { get; set; }
    public string? VerificationCodeHash { get; set; }
    public int CodeAttempts { get; set; }
    public DateTime CodeExpiry { get; set; }
    public string? ResetTokenHash { get; set; }
    public DateTime LinkExpiry { get; set; }
    public SessionState State { get; set; }
    public string? ResolutionReceipt { get; set; }
    public string? EscalationReason { get; set; }
    public long Version { get; set; }
}

public sealed class MockInboxFixture
{
    public string AccountId { get; set; } = null!;
    public string? LastSentCode { get; set; }
    public string? SecureResetLink { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? CodeExpiresAt { get; set; }
    public DateTime? LinkExpiresAt { get; set; }
}

public sealed class ResetResult
{
    public bool Success { get; set; }
    public string? ReceiptId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
}

public interface IRecoverySessionEngine
{
    Task CreateSessionAsync(string callConnectionId, CancellationToken cancellationToken = default);
    Task<RecoverySession?> GetByCallIdAsync(string callConnectionId, CancellationToken cancellationToken = default);
    Task<MockInboxFixture?> GetMockInboxForUserAsync(string userId, CancellationToken cancellationToken = default);
    Task MarkCallDisconnectedAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<bool> ProcessCallInputAsync(
        string sessionId,
        string input,
        string eventId,
        CallConnection? connection,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecoverySession>> GetPendingMonitoringSessionsAsync(CancellationToken cancellationToken = default);
    Task<RecoverySession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<bool> ExpireSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ResetResult> ExecuteAuthoritativeResetAsync(
        string token,
        string rawPassword,
        CancellationToken cancellationToken = default);
}

public sealed class RecoverySessionEngine(
    RecoveryDbContext db,
    IPasswordHasher<IdentityUser> passwordHasher,
    ILogger<RecoverySessionEngine> logger) : IRecoverySessionEngine
{
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromMinutes(10);

    public async Task CreateSessionAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        db.RecoverySessions.Add(new RecoverySession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CallConnectionId = callConnectionId,
            State = SessionState.AwaitingId,
            CodeExpiry = DateTime.UtcNow,
            LinkExpiry = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<RecoverySession?> GetByCallIdAsync(
        string callConnectionId,
        CancellationToken cancellationToken = default) =>
        db.RecoverySessions.SingleOrDefaultAsync(
            session => session.CallConnectionId == callConnectionId,
            cancellationToken);

    public async Task<MockInboxFixture?> GetMockInboxForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var mapping = await db.InboxAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (mapping is null)
        {
            return null;
        }

        var inbox = await db.MockInboxes
            .SingleOrDefaultAsync(item => item.AccountId == mapping.AccountId, cancellationToken);
        if (inbox is null)
        {
            return new MockInboxFixture { AccountId = mapping.AccountId };
        }

        var now = DateTime.UtcNow;
        var changed = false;
        if (inbox.CodeExpiresAt <= now)
        {
            changed |= inbox.LastSentCode is not null;
            inbox.LastSentCode = null;
            inbox.CodeExpiresAt = null;
        }
        if (inbox.LinkExpiresAt <= now)
        {
            changed |= inbox.SecureResetLink is not null;
            inbox.SecureResetLink = null;
            inbox.LinkExpiresAt = null;
        }
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return inbox;
    }

    public async Task MarkCallDisconnectedAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.RecoverySessions.SingleOrDefaultAsync(
            item => item.SessionId == sessionId,
            cancellationToken);
        if (session is null)
        {
            return;
        }

        if (session.State is SessionState.AwaitingId or SessionState.AwaitingCode)
        {
            Escalate(session, "Caller disconnected before verification completed.");
            session.VerificationCodeHash = null;
            await ClearInboxDeliveryAsync(session.AccountId, cancellationToken);
        }
        session.CallConnectionId = null;
        session.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ProcessCallInputAsync(
        string sessionId,
        string input,
        string eventId,
        CallConnection? connection,
        CancellationToken cancellationToken = default)
    {
        string? prompt = null;
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ProcessedCallEvents.AnyAsync(
                item => item.EventId == eventId,
                cancellationToken))
        {
            return false;
        }

        var session = await db.RecoverySessions.SingleOrDefaultAsync(
            item => item.SessionId == sessionId,
            cancellationToken);
        if (session is null || session.State is SessionState.Escalated or SessionState.Completed)
        {
            return false;
        }

        db.ProcessedCallEvents.Add(new ProcessedCallEvent { EventId = eventId, SessionId = sessionId });

        if (IsHumanRequest(input))
        {
            Escalate(session, "Caller requested human support.");
            await ClearInboxDeliveryAsync(session.AccountId, cancellationToken);
            prompt = "Your request has been recorded. No human agent is connected to this exercise. Please contact support for assistance.";
        }
        else if (session.State == SessionState.AwaitingId)
        {
            var accountId = NormalizeDigits(input);
            var account = await db.SyntheticAccounts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.AccountId == accountId, cancellationToken);
            var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

            session.AccountId = accountId;
            session.IsRealAccount = account is not null;
            session.VerificationCodeHash = Hash(code);
            session.CodeAttempts = 0;
            session.CodeExpiry = now.Add(VerificationLifetime);
            session.State = SessionState.AwaitingCode;

            if (account is not null)
            {
                var inbox = await db.MockInboxes.FindAsync([accountId], cancellationToken)
                    ?? new MockInboxFixture { AccountId = accountId };
                inbox.LastSentCode = code;
                inbox.SecureResetLink = null;
                inbox.Timestamp = now;
                inbox.CodeExpiresAt = session.CodeExpiry;
                inbox.LinkExpiresAt = null;
                db.MockInboxes.Update(inbox);
            }

            prompt = "If the supplied account is eligible, a verification code has been sent to its registered recovery inbox. Enter the six-digit code to continue.";
        }
        else if (session.State == SessionState.AwaitingCode)
        {
            var normalizedCode = NormalizeCode(input);
            if (session.CodeExpiry <= now)
            {
                Escalate(session, "Verification code expired.");
                await ClearInboxDeliveryAsync(session.AccountId, cancellationToken);
                prompt = "Your verification code has expired. For your security, this recovery attempt is closed. Please start again or contact support.";
            }
            else if (normalizedCode is null)
            {
                prompt = "Please enter the complete six-digit verification code.";
            }
            else if (!session.IsRealAccount ||
                     session.VerificationCodeHash != Hash(normalizedCode))
            {
                session.CodeAttempts++;
                if (session.CodeAttempts >= 2)
                {
                    Escalate(session, "Verification attempts exhausted.");
                    await ClearInboxDeliveryAsync(session.AccountId, cancellationToken);
                    prompt = "The verification attempt limit has been reached. This recovery attempt is closed. Please contact support.";
                }
                else
                {
                    prompt = "That verification code was not accepted. Please enter the complete six-digit code again.";
                }
            }
            else
            {
                var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                session.VerificationCodeHash = null;
                session.ResetTokenHash = Hash(rawToken);
                session.LinkExpiry = now.Add(ResetLinkLifetime);
                session.State = SessionState.LinkIssued;
                var inbox = await db.MockInboxes.FindAsync([session.AccountId!], cancellationToken)
                    ?? new MockInboxFixture { AccountId = session.AccountId! };
                inbox.SecureResetLink = $"/reset.html?token={rawToken}";
                inbox.Timestamp = now;
                inbox.LinkExpiresAt = session.LinkExpiry;
                db.MockInboxes.Update(inbox);

                prompt = "Your verification is complete. A secure reset link has been sent to your registered recovery inbox. Please open it in a browser to continue.";
            }
        }

        session.Version++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (connection is not null && prompt is not null)
        {
            try
            {
                var media = connection.GetCallMedia();
                if (session.State is SessionState.AwaitingId or SessionState.AwaitingCode)
                {
                    var recognizeOptions = new CallMediaRecognizeSpeechOrDtmfOptions(
                        CommunicationIdentifier.FromRawId(connection.CallConnectionId),
                        maxTonesToCollect: session.State == SessionState.AwaitingId ? 5 : 6)
                    {
                        Prompt = new TextSource(prompt),
                        SpeechLanguage = "en-US",
                        InitialSilenceTimeout = TimeSpan.FromSeconds(10),
                        InterToneTimeout = TimeSpan.FromSeconds(4),
                        EndSilenceTimeout = TimeSpan.FromSeconds(2),
                        StopTones = new[] { DtmfTone.Pound }
                    };
                    await media.StartRecognizingAsync(recognizeOptions, cancellationToken);
                }
                else
                {
                    await media.PlayToAllAsync(new TextSource(prompt), cancellationToken);
                    if (session.State == SessionState.Escalated)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
                        await connection.HangUpAsync(true, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to play recovery prompt for session {SessionId}.", sessionId);
            }
        }

        return true;
    }

    public async Task<IReadOnlyList<RecoverySession>> GetPendingMonitoringSessionsAsync(
        CancellationToken cancellationToken = default) =>
        await db.RecoverySessions.AsNoTracking()
            .Where(session => session.State == SessionState.LinkIssued)
            .ToListAsync(cancellationToken);

    public Task<RecoverySession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        db.RecoverySessions.AsNoTracking()
            .SingleOrDefaultAsync(session => session.SessionId == sessionId, cancellationToken);

    public async Task<bool> ExpireSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.RecoverySessions.SingleOrDefaultAsync(
            item => item.SessionId == sessionId,
            cancellationToken);
        if (session is null || session.State != SessionState.LinkIssued)
        {
            return false;
        }

        Escalate(session, "Reset link expired.");
        session.Version++;
        await ClearInboxDeliveryAsync(session.AccountId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ResetResult> ExecuteAuthoritativeResetAsync(
        string token,
        string rawPassword,
        CancellationToken cancellationToken = default)
    {
        if (rawPassword.Length < 8)
        {
            return new ResetResult
            {
                Success = false,
                ErrorCode = "ERR_PASSWORD_POLICY",
                ErrorDescription = "The new password does not meet the minimum length requirement."
            };
        }

        var tokenHash = Hash(token);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.RecoverySessions.SingleOrDefaultAsync(
            item => item.ResetTokenHash == tokenHash,
            cancellationToken);

        if (session is null)
        {
            return InvalidLink();
        }

        if (session.State == SessionState.Completed && session.ResolutionReceipt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ResetResult { Success = true, ReceiptId = session.ResolutionReceipt };
        }

        if (session.State != SessionState.LinkIssued || session.LinkExpiry <= DateTime.UtcNow ||
            session.AccountId is null)
        {
            return InvalidLink();
        }

        var account = await db.SyntheticAccounts.SingleOrDefaultAsync(
            item => item.AccountId == session.AccountId,
            cancellationToken);
        if (account is null)
        {
            return InvalidLink();
        }

        var receipt = $"REC-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        account.PasswordHash = passwordHasher.HashPassword(
            new IdentityUser { Id = account.AccountId },
            rawPassword);
        session.ResolutionReceipt = receipt;
        session.State = SessionState.Completed;
        session.Version++;

        var inbox = await db.MockInboxes.FindAsync([account.AccountId], cancellationToken);
        if (inbox is not null)
        {
            inbox.LastSentCode = null;
            inbox.SecureResetLink = null;
            inbox.CodeExpiresAt = null;
            inbox.LinkExpiresAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ResetResult { Success = true, ReceiptId = receipt };
    }

    private async Task ClearInboxDeliveryAsync(
        string? accountId,
        CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return;
        }

        var inbox = await db.MockInboxes.FindAsync([accountId], cancellationToken);
        if (inbox is null)
        {
            return;
        }

        inbox.LastSentCode = null;
        inbox.SecureResetLink = null;
        inbox.CodeExpiresAt = null;
        inbox.LinkExpiresAt = null;
    }

    private static string NormalizeDigits(string input)
    {
        var normalized = new StringBuilder(input.Length);
        foreach (Match match in Regex.Matches(input.ToLowerInvariant(), @"\d+|[a-z]+"))
        {
            if (match.Value.All(char.IsDigit))
            {
                normalized.Append(match.Value);
                continue;
            }

            var digit = match.Value switch
            {
                "zero" or "oh" => '0',
                "one" => '1',
                "two" => '2',
                "three" => '3',
                "four" => '4',
                "five" => '5',
                "six" => '6',
                "seven" => '7',
                "eight" => '8',
                "nine" => '9',
                _ => '\0'
            };
            if (digit == '\0')
            {
                return string.Empty;
            }
            normalized.Append(digit);
        }

        return normalized.ToString();
    }

    private static string? NormalizeCode(string input) =>
        NormalizeDigits(input) is { Length: 6 } normalized ? normalized : null;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsHumanRequest(string input) =>
        Regex.IsMatch(input, @"\b(human|agent|representative|operator)\b", RegexOptions.IgnoreCase);

    private static void Escalate(RecoverySession session, string reason)
    {
        session.State = SessionState.Escalated;
        session.VerificationCodeHash = null;
        session.ResetTokenHash = null;
        session.ResolutionReceipt = $"ESC-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        session.EscalationReason = reason;
    }

    private static ResetResult InvalidLink() =>
        new()
        {
            Success = false,
            ErrorCode = "ERR_LINK_INVALID",
            ErrorDescription = "The reset link is invalid, expired, or has already been used."
        };
}
