using eThangAgent.ModelDomain;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Reads and writes the workspace-scoped measured-mix prior through the State
///     Domain's store. The prior's logical key (<see cref="MeasuredMixPrior.StateKey"/>,
///     <c>measured_mix:{provider}:{workspaceRoot}</c>) carries everything the store needs:
///     the namespace is the leading <c>measured_mix</c> segment, the name is the rest
///     (<c>{provider}:{workspaceRoot}</c>), and the store's workspace id IS the
///     <c>{workspaceRoot}</c> segment (the same identity the composition binds as the
///     session's workspace). Reads degrade to null on a missing key or a corrupt payload
///     (the corrupt-to-unset named decision); writes are best-effort — a store fault is
///     swallowed, never propagated into the turn (spec #55 section 4).</summary>
public sealed class MeasuredMixPriorWriter
{
  private readonly IStateStore _store;
  private readonly string _workspaceId;
  private readonly string _ns;
  private readonly string _name;

  public MeasuredMixPriorWriter(IStateStore store, string stateKey)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);

    // measured_mix:{provider}:{workspaceRoot}
    int firstColon = stateKey.IndexOf(':', StringComparison.Ordinal);
    int secondColon = firstColon < 0 ? -1 : stateKey.IndexOf(':', firstColon + 1);
    if (firstColon <= 0 || secondColon < 0 || secondColon == stateKey.Length - 1)
    {
      throw new ArgumentException(
          $"stateKey must be 'measured_mix:{{provider}}:{{workspaceRoot}}'; got '{stateKey}'.", nameof(stateKey));
    }

    _ns = stateKey[..firstColon];
    _name = stateKey[(firstColon + 1)..];
    _workspaceId = stateKey[(secondColon + 1)..];
  }

  /// <summary>The stored prior, or null when the key is missing or its payload is corrupt.</summary>
  public async Task<MeasuredMixPrior?> ReadAsync(CancellationToken ct = default)
  {
    StateKeyValue? row = await _store.GetKeyAsync(_workspaceId, _ns, _name, ct).ConfigureAwait(false);
    return row is not null && MeasuredMixPrior.TryFromJson(row.Value, out MeasuredMixPrior prior)
        ? prior
        : null;
  }

  /// <summary>Persists the prior, best effort: a store fault is swallowed (the caller logs
  ///     through the transcript elsewhere) and never surfaces to the turn.</summary>
  public async Task WriteAsync(MeasuredMixPrior prior, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(prior);
#pragma warning disable CA1031 // Do not catch general exception types — best-effort write per spec #55 section 4.
    try
    {
      _ = await _store.SetKeyCasAsync(_workspaceId, _ns, _name, prior.ToJson(), null, ct).ConfigureAwait(false);
    }
    catch
    {
      // Best-effort: a prior write fault must never break the turn.
    }
#pragma warning restore CA1031
  }
}
