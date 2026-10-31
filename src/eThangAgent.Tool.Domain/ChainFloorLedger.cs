using System.Collections.Concurrent;

namespace eThangAgent.ToolDomain;

/// <summary>One refused act (issue #304): the rule, the command line that tripped it, and the
///     sentence the model saw.</summary>
/// <param name="RuleId">The <see cref="ChainFloorRule.Id"/> that refused it.</param>
/// <param name="Command">The command line, trimmed.</param>
/// <param name="Refusal">The verbatim sentence returned to the model.</param>
public sealed record ChainFloorTrip(string RuleId, string Command, string Refusal);

/// <summary>The turn's record of refused floor acts (issue #304).
///     <para>WHY A LEDGER AND NOT A THROW. The floor refuses an ACT, and the act is a line
///     inside an exec program - throwing there would kill the program mid-way and lose whatever
///     it had already done, which is neither the refusal nor the park the issue asks for. The
///     act is refused in place (the model reads the sentence and can stop), and the PARK happens
///     once per turn from this record, so a program that trips three rules parks the chain once
///     and names the first act.</para>
///     <para>AMBIENT BY CONSTRUCTION, like the workspace anchor scope: the same instance reaches
///     the script globals (which refuse the act) and the loop (which parks the chain), and both
///     read it from one place. A per-container ledger would be invisible to whichever side
///     resolved a different instance.</para>
///     <para>EMPTY IS THE ORDINARY CASE. A session outside a chain, and a chain session that
///     never trips a rule, leave it empty and nothing parks.</para></summary>
public sealed class ChainFloorLedger
{
  private readonly ConcurrentQueue<ChainFloorTrip> _trips = new();

  /// <summary>Records one refused act and returns the sentence the model receives. Called by
  ///     the refusal site itself, so recording and refusing cannot come apart.</summary>
  /// <param name="rule">The tripped rule.</param>
  /// <param name="command">The command line that tripped it.</param>
  public string Trip(ChainFloorRule rule, string command)
  {
    ArgumentNullException.ThrowIfNull(rule);
    ArgumentNullException.ThrowIfNull(command);
    string refusal = ChainFloorRules.Refusal(rule, command);
    _trips.Enqueue(new ChainFloorTrip(rule.Id, command.Trim(), refusal));
    return refusal;
  }

  /// <summary>The acts refused so far, in refusal order. Never null.</summary>
  public IReadOnlyList<ChainFloorTrip> Trips => [.. _trips];

  /// <summary>The FIRST refused act, which is the one a park names: a chain that stopped
  ///     because of one act should report that act, not the queue behind it.</summary>
  public ChainFloorTrip? First => _trips.TryPeek(out ChainFloorTrip? trip) ? trip : null;

  /// <summary>True when any act was refused this turn.</summary>
  public bool Tripped => !_trips.IsEmpty;

  /// <summary>Forgets every refused act. Called at turn start by the same place the
  ///     read-freshness ledger is reset, so one turn's refusal never parks the next turn.
  ///     </summary>
  public void Reset() => _trips.Clear();
}
