using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Everything a chain step's session is launched with (issue #311): the seeded brief
///     it works from, and the contract its system prompt carries.
///     <para>THE TWO ARE DIFFERENT KINDS OF FACT and neither replaces the other. The seed is a
///     MESSAGE — it scrolls out of attention and a compaction can evict it. The contract is
///     PROMPT TEXT — it is in every request. A session that had only the seed would forget it is
///     a chain step; a session that had only the contract would not know what to do.</para>
///     <para>BOTH COME FROM ONE GATHER (<see cref="ChainBriefService.BuildLaunchAsync"/>), so the
///     ledger key and the gate are the same values in both rather than two derivations that
///     happen to agree.</para></summary>
/// <param name="Seed">The seeded prefix: the rendered brief as one summary message.</param>
/// <param name="Contract">The chain contract derived from the same facts, or null when the
///     launch is not a chain step at all (a compaction handoff rides the same launch path).</param>
public sealed record ChainBriefLaunch(IReadOnlyList<Message> Seed, ChainContract? Contract);
