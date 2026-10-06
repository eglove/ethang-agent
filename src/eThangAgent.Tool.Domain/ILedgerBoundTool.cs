namespace eThangAgent.ToolDomain;

/// <summary>A tool that can carry the agent's turn-local read-freshness ledger
///     (issue #117). The loop owns one ledger per agent — children get their own, so
///     a child's read never elides against the root's memory — binds it at dispatch
///     through this seam, and resets it at turn start and wherever conversation
///     history is replaced (compaction, context shrink). Implementations bind the
///     ledger once; HasLedger lets the loop skip re-binding.</summary>
public interface ILedgerBoundTool
{
  /// <summary>True when a ledger is already bound.</summary>
  bool HasLedger { get; }

  /// <summary>Binds the agent's ledger; returns the tool to dispatch (this or a copy).</summary>
  ITool WithLedger(ReadFreshnessLedger ledger);
}
