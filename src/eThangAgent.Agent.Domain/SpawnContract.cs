using System.Text.Json;

namespace eThangAgent.AgentDomain;

/// <summary>Budget ceilings for a child run. Null members are unbounded — the domain
///     default; the host supplies generous session policy (spec open question 4, resolved).</summary>
public sealed record BudgetCeilings(long? MaxTokens = null, decimal? MaxCost = null, long? MaxToolCalls = null);

/// <summary>The spawn-time agreement (source spec Section 4.5): persisted with the record
///     so resume and audit see the contract the run started with. T3 members exist now,
///     defaulted — the ladder adds enforcement, not shape.</summary>
public sealed record SpawnContract(
    string? ResultSchema = null,
    IReadOnlyDictionary<string, string>? CapabilityGrants = null,
    BudgetCeilings? Budgets = null,
    int MaxUrgency = 0,
    bool PreemptGrant = false,
    string? EffectiveTools = null)
{
  /// <summary>The dispatch-time effective tool set R1 enforces: resolved by the spawn
  ///     command from the parent's effective set and the validated grants, persisted so
  ///     the record is self-describing (resume/audit see the agreement the run started
  ///     with) and the remote ChildHost path enforces identically without SpawnOptions.
  ///     Semicolon-joined action ids; null = no resolved grants (default surface).</summary>
  public IReadOnlySet<string>? DecodedEffectiveTools
      => EffectiveTools?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .ToHashSet(StringComparer.Ordinal);

  /// <summary>Persists the resolved effective set onto this contract (ordinal-ordered).</summary>
  public SpawnContract WithEffectiveTools(IReadOnlySet<string> effective)
      => this with { EffectiveTools = string.Join(";", effective.OrderBy(n => n, StringComparer.Ordinal)) };

  private static readonly JsonSerializerOptions Options = new();

  /// <summary>Serializes the contract for the record's Contract column. Null-safe.</summary>
  public static string? Encode(SpawnContract? contract)
      => contract is null ? null : JsonSerializer.Serialize(contract, Options);

  /// <summary>Deserializes a persisted contract. Throws JsonException on malformed input —
  ///     a corrupt contract column is an infrastructure fault, never silently dropped.</summary>
  public static SpawnContract Decode(string json)
      => JsonSerializer.Deserialize<SpawnContract>(json, Options)
          ?? throw new JsonException("SpawnContract payload deserialized to null.");
}
