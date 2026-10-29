using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container holder for the chain contract this session runs under (issue #311),
///     the same construction-phase glue <see cref="SessionFilesCarrier"/> and
///     <see cref="TrustedDirectoriesCarrier"/> are: the launcher fills it when it starts a
///     session FOR a chain step, and the RemoteHost block overlays the value onto the settings
///     JSON the host receives — because the host process runs every production turn, and a
///     contract that did not travel would leave the prompt silent in the only process that
///     serves the model.
///     <para>MUTABLE BY DESIGN: construction-phase glue, filled once before the container hands
///     the session out. A contract that changed mid-session would be a prompt that changed
///     mid-session, which is the cache defect <see cref="ISystemPromptProvider"/>
///     exists to prevent.</para>
///     <para>NULL IS THE ORDINARY CASE, not an error: a session a user started by hand belongs
///     to no chain, and the provider then renders nothing at all.</para></summary>
public sealed class ChainContractCarrier
{
  /// <summary>The contract this session runs under, or null when it is not a chain step.</summary>
  public ChainContract? Contract { get; private set; }

  /// <summary>Fills the carrier. Called once, at container build.</summary>
  /// <param name="contract">The chain contract, or null for a session outside a chain.</param>
  public void Fill(ChainContract? contract) => Contract = contract;
}
