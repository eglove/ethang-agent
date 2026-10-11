using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container holder for the measured-mix dynamic provider selection stack
///     (spec #55): the seeded policy, the workspace-prior writer, and the usage observer
///     the root agent publishes provider usage to. The factory fills it after its
///     preference and prior reads; the composition's root-holder and handler
///     registrations read it lazily at construction. Empty (all null) means the session
///     is NOT a Price-sort session: byte-identical wiring, nothing constructed.
///     Mutable by design: construction-phase glue, like <see cref="SmartSelectionCarrier"/>.</summary>
public sealed class MeasuredMixCarrier
{
  /// <summary>The seeded policy, or null when the session is not Price-sorted.</summary>
  public MeasuredMixPolicy? Policy { get; private set; }

  /// <summary>The workspace-prior writer, or null when the session is not Price-sorted.</summary>
  public MeasuredMixPriorWriter? Writer { get; private set; }

  /// <summary>The usage observer the root agent publishes to, or null when the session
  ///     is not Price-sorted.</summary>
  public Action<TokenUsage>? OnUsage { get; private set; }

  public void Fill(MeasuredMixPolicy? policy, MeasuredMixPriorWriter? writer, Action<TokenUsage>? onUsage)
  {
    Policy = policy;
    Writer = writer;
    OnUsage = onUsage;
  }
}
