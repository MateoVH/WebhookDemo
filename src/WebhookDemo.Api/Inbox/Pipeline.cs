using WebhookDemo.Data;

namespace WebhookDemo.Inbox;

/// <summary>A business flow triggered by an event, split into named steps that are checkpointed one by one.</summary>
public interface IWebhookPipeline
{
    bool CanHandle(string source, string eventType);

    /// <summary>
    /// The steps for this event, in order. Step names are stored in checkpoints, so keep them stable:
    /// renaming a step makes in-flight events run it again.
    /// Throws <see cref="NonRetryableException"/> when the payload can't be processed at all.
    /// </summary>
    IReadOnlyList<PipelineStep> Plan(InboxEvent evt);
}

public enum StepKind
{
    /// <summary>Only writes to our database. It commits in the same transaction as its checkpoint: all or nothing.</summary>
    Local,

    /// <summary>
    /// Calls another system, which can't join our transaction. If the worker dies after the call and before the
    /// checkpoint, the call runs again, so it has to be idempotent on the other side (idempotency key or upsert).
    /// </summary>
    External,
}

public sealed class PipelineStep
{
    private readonly Func<WebhookDbContext, StepContext, CancellationToken, Task<string?>>? _local;
    private readonly Func<StepContext, CancellationToken, Task<string?>>? _external;

    private PipelineStep(
        string name,
        StepKind kind,
        Func<WebhookDbContext, StepContext, CancellationToken, Task<string?>>? local,
        Func<StepContext, CancellationToken, Task<string?>>? external)
    {
        Name = name;
        Kind = kind;
        _local = local;
        _external = external;
    }

    public string Name { get; }
    public StepKind Kind { get; }

    /// <summary>
    /// A step that stages changes on the given DbContext. The processor saves them and commits them
    /// together with the checkpoint.
    /// </summary>
    public static PipelineStep Local(string name, Func<WebhookDbContext, StepContext, CancellationToken, Task<string?>> run) =>
        new(name, StepKind.Local, run, null);

    public static PipelineStep External(string name, Func<StepContext, CancellationToken, Task<string?>> run) =>
        new(name, StepKind.External, null, run);

    internal Task<string?> RunLocalAsync(WebhookDbContext db, StepContext context, CancellationToken ct) =>
        _local!(db, context, ct);

    internal Task<string?> RunExternalAsync(StepContext context, CancellationToken ct) =>
        _external!(context, ct);
}

/// <summary>
/// What a step gets to see: the event, and the results of the steps that already completed,
/// in this attempt or in an earlier one.
/// </summary>
public sealed class StepContext(InboxEvent evt, IReadOnlyDictionary<string, string?> completed)
{
    public InboxEvent Event { get; } = evt;

    public string? ResultOf(string step) => completed.GetValueOrDefault(step);
}

/// <summary>
/// A failure that retrying can't fix: a malformed payload, a request the provider rejected…
/// The event goes straight to the dead-letter queue.
/// </summary>
public sealed class NonRetryableException(string message, Exception? inner = null) : Exception(message, inner);
