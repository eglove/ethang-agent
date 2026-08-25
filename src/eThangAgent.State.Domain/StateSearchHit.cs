namespace eThangAgent.StateDomain;

/// <summary>One full-text search hit over workspace state: the key that matched
/// and a snippet of the matching value with match highlighting.</summary>
public sealed record StateSearchHit(string Ns, string Name, string Snippet);
