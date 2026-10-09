namespace WebhookDemo.Inbox;

/// <summary>The moments around a step where a failure hurts. The processor asks the chaos monkey at each one.</summary>
public enum ChaosPoint
{
    /// <summary>Before the step starts (the provider is down, say).</summary>
    BeforeStep,

    /// <summary>The step did its work but its checkpoint isn't saved yet: the worst moment to die.</summary>
    AfterWork,

    /// <summary>The step and its checkpoint are saved; the next step hasn't started.</summary>
    AfterCheckpoint,
}

public enum ChaosAction
{
    None,

    /// <summary>The process dies on the spot (simulated).</summary>
    Crash,

    /// <summary>A failure that may go away on its own (HTTP 503, a timeout): retried with backoff.</summary>
    TransientError,

    /// <summary>A failure that won't go away (HTTP 422): dead-lettered.</summary>
    PermanentError,
}

/// <summary>
/// Fault injection. The demo and the tests use it to prove that recovery works; production uses <see cref="NoChaos"/>.
/// </summary>
public interface IChaosMonkey
{
    ChaosAction Decide(ChaosPoint point, InboxEvent evt, string step);
}

public sealed class NoChaos : IChaosMonkey
{
    public ChaosAction Decide(ChaosPoint point, InboxEvent evt, string step) => ChaosAction.None;
}

/// <summary>Simulates the process dying. The processor deliberately does no cleanup when it sees one.</summary>
public sealed class SimulatedCrashException(ChaosPoint point, string step)
    : Exception($"Simulated crash: {point} '{step}'")
{
    public ChaosPoint Point { get; } = point;
    public string Step { get; } = step;
}

public sealed class SimulatedTransientException(string step)
    : Exception($"Simulated transient failure in '{step}': the provider answered 503 Service Unavailable");

/// <summary>Chaos from explicit rules such as "crash after issue-invoice, once, for event evt_123". Thread-safe.</summary>
public sealed class ScriptedChaosMonkey : IChaosMonkey
{
    private readonly List<ChaosRule> _rules = [];
    private readonly Lock _lock = new();

    public void Add(ChaosRule rule)
    {
        lock (_lock) _rules.Add(rule);
    }

    public void Clear()
    {
        lock (_lock) _rules.Clear();
    }

    public ChaosAction Decide(ChaosPoint point, InboxEvent evt, string step)
    {
        lock (_lock)
        {
            var rule = _rules.Find(r => r.Matches(point, evt, step));
            if (rule is null) return ChaosAction.None;

            if (--rule.Remaining == 0) _rules.Remove(rule);
            return rule.Action;
        }
    }
}

/// <param name="times">How many times the rule fires before it's used up.</param>
/// <param name="externalId">Only fire for this event; null fires for any event.</param>
public sealed class ChaosRule(ChaosPoint point, string step, ChaosAction action, int times = 1, string? externalId = null)
{
    public ChaosPoint Point { get; } = point;
    public string Step { get; } = step;
    public ChaosAction Action { get; } = action;
    public string? ExternalId { get; } = externalId;
    internal int Remaining { get; set; } = times;

    internal bool Matches(ChaosPoint point, InboxEvent evt, string step) =>
        Point == point && Step == step && (ExternalId is null || ExternalId == evt.ExternalId);
}
