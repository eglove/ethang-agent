namespace eThangAgent.AgentDomain;

/// <summary>The first turn a chain step's session takes (issue #323's product decision).
///     <para>WHY A KICKOFF LINE AND NOT THE BRIEF AS A USER MESSAGE: the brief is ALREADY the
///     session's seeded prefix - a System message flagged as a summary - and the chain contract
///     is already in its system prompt. Firing the brief again as a user turn would state the
///     same instructions twice, in two voices, and both <c>executing-a-chain</c> and
///     <c>experiment-driven-development</c> describe the brief as a seeded prefix rather than a
///     request. One short line gives the model an unambiguous start without a second copy.</para>
///     <para>WHY A HARDCODED HARNESS STRING IS THE HONEST SHAPE: #313's D5 forbids a handoff
///     written by a model, and this is the opposite - a constant a human wrote, reviewed and can
///     change. It is not a summary of anything and it decides nothing; it only says 'begin'.</para>
///     <para>ROLE-NEUTRAL ON PURPOSE. An implementer and a reviewer both begin; what they begin
///     is stated by the brief and the contract, which already branch on role (#306). A second
///     role-aware sentence here would be a third place the role is described.</para>
///     <para>IT IS NOT A GENERAL AUTO-FIRE. A session launched with no prompt - the ordinary
///     'open a workspace' launch, and the compaction handoff, which continues from its summary
///     rather than re-asking - still fires no turn. Only a chain step, which is launched FOR a
///     piece of work, is kicked off.</para></summary>
public static class ChainKickoff
{
  /// <summary>The line a chain step's session is started with.</summary>
  public const string Line = "Begin the step described in your brief.";
}
