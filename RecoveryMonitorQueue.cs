using System.Collections.Concurrent;

namespace AccountRecoveryApi.Infrastructure;

public interface IRecoveryMonitorQueue
{
    void QueueSessionForMonitoring(string sessionId);
    bool TryDequeue(out string? sessionId);
}

public class RecoveryMonitorQueue : IRecoveryMonitorQueue
{
    private readonly ConcurrentQueue<string> _queue = new();

    public void QueueSessionForMonitoring(string sessionId) => _queue.Enqueue(sessionId);

    public bool TryDequeue(out string? sessionId) => _queue.TryDequeue(out sessionId);
}
