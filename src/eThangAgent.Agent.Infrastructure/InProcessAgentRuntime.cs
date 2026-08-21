using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure;

/// <summary>In-process actor runtime: every accepted child runs to completion on one background task
/// while the caller continues. A strict concurrency cap is enforced with a zero-timeout slot wait —
/// at-capacity starts fail with <see cref="RuntimeErrors.CapReached"/> and produce no side effects.</summary>
public sealed class InProcessAgentRuntime : IAgentRuntime
{
    private readonly IAgentRunner _runner;
    private readonly IAgentStore _store;
    private readonly SemaphoreSlim _slots;

    public InProcessAgentRuntime(IAgentRunner runner, IAgentStore store, int maxConcurrentAgents)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(store);
        if (maxConcurrentAgents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentAgents), maxConcurrentAgents,
                "MaxConcurrentAgents must be at least 1.");
        _runner = runner;
        _store = store;
        _slots = new SemaphoreSlim(maxConcurrentAgents, maxConcurrentAgents);
    }

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
    {
        if (!_slots.Wait(0))
            return Task.FromResult(Result<AgentId>.Failure(CapError()));

        _ = Task.Run(() => RunToCompletionAsync(record));
        return Task.FromResult(Result<AgentId>.Success(record.Id));
    }

    private async Task RunToCompletionAsync(AgentRecord record)
    {
        try
        {
            var outcome = await _runner.RunAsync(record);
            await _store.UpdateAsync(record with
            {
                Status = outcome.Status,
                FailureReason = outcome.Reason,
                CompletedAt = DateTimeOffset.UtcNow,
                FinalReport = outcome.Report,
            });
        }
        catch (Exception ex)
        {
            // Runner faults are terminal child outcomes, not crashes: persist them so the parent
            // can retrieve a well-formed failure via agent.result.
            await _store.UpdateAsync(record with
            {
                Status = AgentStatus.Failed,
                FailureReason = AgentFailureReason.ProviderError,
                CompletedAt = DateTimeOffset.UtcNow,
                FinalReport = "Error [ProviderError]: " + ex.Message,
            });
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Translates the canonical CapReached string into an <see cref="Error"/> without duplicating
    /// its text: "Error [Code]: message" becomes Error(Code, message), so downstream pass-through rendering
    /// reproduces <see cref="RuntimeErrors.CapReached"/> byte-for-byte.</summary>
    private static Error CapError()
    {
        var canonical = RuntimeErrors.CapReached;
        var codeStart = canonical.IndexOf('[') + 1;
        var codeEnd = canonical.IndexOf(']', codeStart);
        return new Error(canonical[codeStart..codeEnd], canonical[(codeEnd + 3)..]);
    }
}
