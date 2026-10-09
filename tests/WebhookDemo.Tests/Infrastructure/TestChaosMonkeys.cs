using WebhookDemo.Inbox;

namespace WebhookDemo.Tests.Infrastructure;

/// <summary>Crashes workers and makes providers fail at random, reproducibly (fixed seed).</summary>
public sealed class RandomChaosMonkey(int seed, double crashRate, double transientErrorRate) : IChaosMonkey
{
    private readonly Random _random = new(seed);
    private readonly Lock _lock = new();

    public int Crashes { get; private set; }
    public int TransientErrors { get; private set; }

    public ChaosAction Decide(ChaosPoint point, InboxEvent evt, string step)
    {
        lock (_lock)
        {
            var roll = _random.NextDouble();
            if (roll < crashRate)
            {
                Crashes++;
                return ChaosAction.Crash;
            }
            if (point == ChaosPoint.BeforeStep && roll < crashRate + transientErrorRate)
            {
                TransientErrors++;
                return ChaosAction.TransientError;
            }
            return ChaosAction.None;
        }
    }
}

/// <summary>Runs arbitrary test code at a chaos point, e.g. to let another worker act while this one is paused.</summary>
public sealed class CallbackChaosMonkey(Func<ChaosPoint, InboxEvent, string, ChaosAction> decide) : IChaosMonkey
{
    public ChaosAction Decide(ChaosPoint point, InboxEvent evt, string step) => decide(point, evt, step);
}
