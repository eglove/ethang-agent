namespace eThangAgent.MemoryDomain;

/// <summary>Which lineage branches contribute entries to a recall.</summary>
public enum BranchMode
{
  /// <summary>Only sessions in an active lineage. Every session is its own lineage root, so this
  ///     keeps the whole corpus (issue #334); the mode remains so a caller that asked for it keeps
  ///     one meaning rather than being silently reinterpreted.</summary>
  ActivePath,

  /// <summary>Every session in scope, including orphan chains.</summary>
  AllBranches
}
