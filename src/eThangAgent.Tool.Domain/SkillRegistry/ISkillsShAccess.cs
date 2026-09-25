using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The skills.sh JSON API seam (plan #29 task 5, adapted per ledger
/// v57): GET https://www.skills.sh/api/search?q=&lt;term&gt; returns the
/// directory's JSON. Entry resolution uses the same endpoint (an entry-name
/// query lists the entry's sub-skills). Implemented in the Web ACL.</summary>
public interface ISkillsShAccess
{
  /// <summary>Fetches the search/entry JSON for one query term; returns the
  /// raw JSON text for SkillsShSearchParser.</summary>
  Task<Result<string>> FetchSearchAsync(string query, CancellationToken ct = default);
}