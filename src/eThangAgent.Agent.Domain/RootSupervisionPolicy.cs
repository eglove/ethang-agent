namespace eThangAgent.AgentDomain;

/// <summary>What the harness does when a ROOT session goes idle without completing its
///     assigned step (issue #301 D5): nudge once, then park.
///     <para>THE LADDER IS THE WATCHDOG'S, THE TERMINAL IS NOT. <see cref="WatchdogPolicy"/>
///     escalates a stuck CHILD to a retry and then to <c>Failed(Hung)</c>, because a child is a
///     run that can be restarted. A chain step's session is not: parking the CHAIN is the
///     honest outcome, because the work is waiting for a human rather than broken (#303 D3).
///     So the attempt budget is shared and the last rung differs.</para>
///     <para>PURE: attempts in, decision out. The nudge text and the park call are the
///     application layer's, so this rule is testable without a clock, a store, or a runtime.</para></summary>
public static class RootSupervisionPolicy
{
  /// <summary>Decides the rung for a root session that has crossed its idle threshold.
  ///     <paramref name="wrapUpAttempts"/> counts nudges already spent on this session.</summary>
  /// <param name="wrapUpAttempts">Nudges already spent on this session.</param>
  /// <param name="maxWrapUpAttempts">The budget, from the same <see cref="WatchdogPolicy"/>
  ///     the child ladder uses, so one configured knob governs both.</param>
  public static RootSupervisionDecision Decide(int wrapUpAttempts, int maxWrapUpAttempts)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(wrapUpAttempts);
    ArgumentOutOfRangeException.ThrowIfNegative(maxWrapUpAttempts);
    return wrapUpAttempts >= maxWrapUpAttempts
        ? RootSupervisionDecision.Park
        : RootSupervisionDecision.Nudge;
  }

  /// <summary>The nudge a chain step's session receives when it goes idle without completing.
  ///     <para>DELIBERATELY NOT <see cref="WatchdogPolicy.WrapUpNudge"/>: that message is about
  ///     idleness and tells a CHILD to wrap up and produce a final report. A chain step's
  ///     session owes a COMPLETION, not a report - it must mark its assigned step Done, and a
  ///     nudge that asked for a report would produce exactly the wrong artifact.</para>
  ///     <para>It names the step and the chain so the session cannot mistake which work is
  ///     outstanding, and it says who acts next, because a session that does not know the
  ///     harness is waiting will keep waiting itself.</para></summary>
  public static string Nudge(int chainId, int position)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(chainId, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"[harness] You have been idle without completing chain #{chainId} step {position}, which is the step you were started for. The harness advances the chain when you mark that step Done; nothing else will move it, and it will not start the next step until you do. Continue from where you stopped and mark the step Done when the work is finished - or, if you are blocked, say so plainly in your next reply so the chain can be parked for a human.");
  }
}

/// <summary>The rung a root-session idle breach lands on.</summary>
public enum RootSupervisionDecision
{
  /// <summary>Send the nudge and wait for the next breach.</summary>
  Nudge,

  /// <summary>The nudge budget is spent: park the chain, naming the step and the reason.
  ///     A parked chain is waiting for a human, never failed.</summary>
  Park,
}
