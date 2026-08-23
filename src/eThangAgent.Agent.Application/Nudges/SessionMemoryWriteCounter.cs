namespace eThangAgent.Agent.Application.Nudges;

/// <summary>
/// Explicit session-scoped seam for the curated-memory write count, separating its two
/// consumers: the capability provider bumps via <see cref="Increment"/> on every
/// successful add, while the turn-boundary nudge path reads via <see cref="Count"/>.
/// A shared <c>Func&lt;int&gt;</c> made every read increment too — the count drifted with
/// each evaluation and DefaultNudgePolicy's zero condition could never hold again, so
/// nudges were dead in production while unit fakes (constant funcs) stayed green. The
/// dedicated type makes the read side-effect-free by construction.
/// </summary>
public sealed class SessionMemoryWriteCounter
{
    private int _count;

    /// <summary>Records one successful memory write; called only by the provider.</summary>
    public int Increment() => Interlocked.Increment(ref _count);

    /// <summary>Observes the current write total; side-effect-free.</summary>
    public int Count => Volatile.Read(ref _count);
}
