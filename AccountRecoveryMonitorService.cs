using AccountRecoveryApi.Domain;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AccountRecoveryApi.Infrastructure;

public sealed class AccountRecoveryMonitorService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    ILogger<AccountRecoveryMonitorService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTime> _activeSessions = new();
    private bool _missingCallClientLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Persistent recovery-session monitor started.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        try
        {
            do
            {
                await LoadPendingSessionsAsync(stoppingToken);
                foreach (var (sessionId, expiry) in _activeSessions.ToArray())
                {
                    await ProcessSessionAsync(sessionId, expiry, stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task LoadPendingSessionsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IRecoverySessionEngine>();
        var sessions = await engine.GetPendingMonitoringSessionsAsync(cancellationToken);
        foreach (var session in sessions)
        {
            _activeSessions.TryAdd(session.SessionId, session.LinkExpiry);
        }
    }

    private async Task ProcessSessionAsync(
        string sessionId,
        DateTime expiry,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IRecoverySessionEngine>();
        var session = await engine.GetSessionAsync(sessionId, cancellationToken);
        if (session is null || session.State is SessionState.Escalated or SessionState.AwaitingId or SessionState.AwaitingCode)
        {
            _activeSessions.Remove(sessionId);
            return;
        }

        if (session.State == SessionState.Completed && !string.IsNullOrEmpty(session.ResolutionReceipt))
        {
            await PlayMessageAndHangUpAsync(
                session,
                $"Your synthetic account reset is complete. Receipt: {session.ResolutionReceipt}.",
                cancellationToken);
            _activeSessions.Remove(sessionId);
            return;
        }

        if (session.State != SessionState.LinkIssued)
        {
            _activeSessions.Remove(sessionId);
            return;
        }

        if (expiry <= DateTime.UtcNow)
        {
            if (await engine.ExpireSessionAsync(sessionId, cancellationToken))
            {
                logger.LogInformation("Recovery session {SessionId} expired and was escalated.", sessionId);
                await PlayMessageAndHangUpAsync(
                    session,
                    "Your recovery session has expired. For your security, this call will now end. Please start again or contact support.",
                    cancellationToken);
            }
            _activeSessions.Remove(sessionId);
            return;
        }

        if (session.CallConnectionId is not null &&
            !await IsCallConnectedAsync(session.CallConnectionId))
        {
            await engine.MarkCallDisconnectedAsync(sessionId, cancellationToken);
        }
    }

    private async Task<bool> IsCallConnectedAsync(string callConnectionId)
    {
        var callClient = GetCallAutomationClient();
        if (callClient is null)
        {
            return true;
        }

        try
        {
            var properties = await callClient
                .GetCallConnection(callConnectionId)
                .GetCallConnectionPropertiesAsync();
            return properties?.Value?.CallConnectionState == CallConnectionState.Connected;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to check call {CallConnectionId}; the monitor will retry.", callConnectionId);
            return true;
        }
    }

    private async Task PlayMessageAndHangUpAsync(
        RecoverySession session,
        string message,
        CancellationToken cancellationToken)
    {
        var callClient = GetCallAutomationClient();
        if (callClient is null || session.CallConnectionId is null)
        {
            return;
        }

        CallConnection? connection = null;
        try
        {
            connection = callClient.GetCallConnection(session.CallConnectionId);
            await connection.GetCallMedia().PlayToAllAsync(new TextSource(message), cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to play call message for recovery session {SessionId}.", session.SessionId);
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.HangUpAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to hang up call for recovery session {SessionId}.", session.SessionId);
        }
    }

    private CallAutomationClient? GetCallAutomationClient()
    {
        var client = services.GetService<CallAutomationClient>();
        if (client is null && !_missingCallClientLogged)
        {
            logger.LogError("CallAutomationClient is unavailable; call monitoring actions cannot be performed.");
            _missingCallClientLogged = true;
        }

        return client;
    }
}
