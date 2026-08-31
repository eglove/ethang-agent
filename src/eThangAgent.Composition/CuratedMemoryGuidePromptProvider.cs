using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>
/// Usage guidance for the curated-memory capability surface, appended after the exec
/// guide so the model knows when to search and when to write curated memories.
/// Guidance only — it never instructs the model to store anything beyond the durable-
/// fact rules stated here.
/// </summary>
public sealed class CuratedMemoryGuidePromptProvider : ISystemPromptProvider
{
  public string Build() =>
      """
        Persistent curated memories: you maintain a searchable knowledge base of durable facts —
        conventions, preferences, insights, failures, references — via the memories actions
        (memories.search / memories.add / memories.update / memories.remove / memories.purge
        through exec). Search before adding — near-duplicate adds are rejected, so update the
        existing entry instead. Purge stale or contradicted memories when you notice them
        (memories.purge with dry_run true first shows what would go). Search when context feels missing before assuming;
        write when the user states a durable preference/convention or a task reveals a non-obvious
        insight or failure worth remembering. Keep entries atomic (one fact each), tagged, and
        scoped honestly (global only for facts true everywhere). Never store secrets, transient
        task state, or anything derivable from the repo. After completing a genuinely complex
        multi-step effort, consider proposing a reusable skill via skill_manage (source learned)
        capturing what generalizes beyond this workspace.
        """;
}
