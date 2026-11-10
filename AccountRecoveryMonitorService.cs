using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Azure.Communication.CallAutomation;
using AccountRecoveryApi.Domain;

namespace AccountRecoveryApi.Infrastructure;

public class AccountRecoveryMonitorService : BackgroundService
{
    private readonly IRecoveryMonitorQueue _monitorQueue;
    private readonly IMemoryCache _cache;
    private readonly IServiceProvider _services;
    private readonly ILogger<AccountRecoveryMonitorService> _logger;
    private bool _missingCallClientLogged;
    
    // In-memory active tracking register managed by the single thread loop
    private readonly Dictionary<string, (DateTime Expiry, int PollCount)> _activeTrackers = new();
    private const string SessionPrefix = "rec_sess_";
    private const string CallPrefix = "call_idx_";
    private const string TokenPrefix = "tok_idx_";

    public AccountRecoveryMonitorService(
        IRecoveryMonitorQueue monitorQueue, 
        IMemoryCache cache,
        IServiceProvider services,
        ILogger<AccountRecoveryMonitorService> logger)
    {
        _monitorQueue = monitorQueue;
        _cache = cache;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("[BACKGROUND WORKER] Authoritative Web Resolution Engine initialized.");

        // Periodic ticker waking up every 3 seconds to process active monitoring steps
        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. Drain any new sessions added to the tracking queue without blocking
            while (_monitorQueue.TryDequeue(out var newSessionId))
            {
                if (newSessionId != null)
                {
                    _activeTrackers[newSessionId] = (DateTime.UtcNow.AddMinutes(10), 0);
                }
            }

            // 2. Perform a single batch pass over all active calls
            foreach (var (sessionId, (expiry, pollCount)) in _activeTrackers.ToList())
            {
                if (DateTime.UtcNow > expiry || pollCount >= 200)
                {
                    await ProcessTimeoutAsync(sessionId);
                    _activeTrackers.Remove(sessionId);
                    continue;
                }

                bool shouldKeepTracking = await EvaluateSessionStateAsync(sessionId);
                if (!shouldKeepTracking)
                {
                    _activeTrackers.Remove(sessionId);
                }
                else
                {
                    // Increment the tick iteration count
                    _activeTrackers[sessionId] = (expiry, pollCount + 1);
                }
            }

            // 3. Sleep efficiently until the next iteration tick
            await periodicTimer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task<bool> EvaluateSessionStateAsync(string sessionId)
    {
        if (!_cache.TryGetValue($"{SessionPrefix}{sessionId}", out RecoverySession? session) || session == null)
            return false; // Evicted out-of-band, terminate tracking loop

        if (session.State == SessionState.Escalated) 
            return false;

        if (session.State == SessionState.Completed && !string.IsNullOrEmpty(session.ResolutionReceipt))
        {
            await ExecuteSuccessPlaybackAsync(session);
            return false; // Resolved successfully, stop tracking
        }

        // Handle Call Disconnection Check natively to prevent Ghost tasks
        return await CheckCallSignalingActiveAsync(session.CallConnectionId);
    }

    private async Task<bool> CheckCallSignalingActiveAsync(string callConnectionId)
    {
        var callClient = GetCallAutomationClient();
        if (callClient == null)
        {
            return true;
        }

        try
        {
            var connection = callClient.GetCallConnection(callConnectionId);
            var properties = await connection.GetCallConnectionPropertiesAsync();
            return properties?.Value?.CallConnectionState == CallConnectionState.Connected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to check call state for connection {CallConnectionId}; monitoring will retry.", callConnectionId);
            return true;
        }
    }

    private async Task ExecuteSuccessPlaybackAsync(RecoverySession session)
    {
        var callClient = GetCallAutomationClient();
        if (callClient == null)
        {
            return;
        }

        try
        {
            var connection = callClient.GetCallConnection(session.CallConnectionId);
            var media = connection.GetCallMedia();
            await media.PlayToAllAsync(new TextSource($"Transaction completed. Ticket reference: {session.ResolutionReceipt}."));
            await Task.Delay(TimeSpan.FromSeconds(5)); // Brief delivery window
            await connection.HangUpAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to play recovery completion message for session {SessionId}.", session.SessionId);
        }
    }

    private CallAutomationClient? GetCallAutomationClient()
    {
        var callClient = _services.GetService<CallAutomationClient>();
        if (callClient == null && !_missingCallClientLogged)
        {
            _logger.LogError("CallAutomationClient is unavailable; call monitoring actions cannot be performed.");
            _missingCallClientLogged = true;
        }

        return callClient;
    }

    private async Task ProcessTimeoutAsync(string sessionId)
    {
        if (_cache.TryGetValue($"{SessionPrefix}{sessionId}", out RecoverySession? session) && session != null)
        {
            session.State = SessionState.Escalated;
            _cache.Set($"{SessionPrefix}{sessionId}", session, TimeSpan.FromMinutes(15));
            _cache.Remove($"{CallPrefix}{session.CallConnectionId}");
            if (!string.IsNullOrEmpty(session.ResetToken))
            {
                _cache.Remove($"{TokenPrefix}{session.ResetToken}");
            }

            _logger.LogInformation("Recovery session {SessionId} expired and was escalated.", sessionId);

            var callClient = GetCallAutomationClient();
            if (callClient != null)
            {
                CallConnection? connection = null;
                try
                {
                    connection = callClient.GetCallConnection(session.CallConnectionId);
                    await connection.GetCallMedia().PlayToAllAsync(new TextSource(
                        "Your recovery session has expired. For your security, this call will now end. Please start again or contact support."));
                    await Task.Delay(TimeSpan.FromSeconds(8));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to play timeout message for session {SessionId}. The call will still be ended.", sessionId);
                }

                if (connection != null)
                {
                    try
                    {
                        await connection.HangUpAsync(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to hang up timed-out recovery call for session {SessionId}.", sessionId);
                    }
                }
            }
        }
    }
}
