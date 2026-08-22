namespace eThangAgent.MemoryDomain;

/// <summary>Which lineage branches contribute entries to a recall.</summary>
public enum BranchMode
{
    /// <summary>Only sessions whose ParentId chain terminates at a root within the corpus.</summary>
    ActivePath,

    /// <summary>Every session in scope, including orphan chains.</summary>
    AllBranches
}
