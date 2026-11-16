using System.Collections.Concurrent;
using Framework.Security;

namespace Framework.Web.AI;

/// <summary>Shares an in-memory hourly request budget between AI features for each tenant user.</summary>
public sealed class AiRequestRateLimiter(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Demand(UserContext user, int requestsPerHour)
    {
        var limit = Math.Max(1, requestsPerHour);
        var now = _clock.GetUtcNow();
        var queue = _recent.GetOrAdd($"{user.TenantId}|{user.Id}", _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
                queue.Dequeue();
            if (queue.Count >= limit)
                throw new InvalidOperationException($"AI request limit reached ({limit} per hour). Try again later.");
            queue.Enqueue(now);
        }
    }
}
