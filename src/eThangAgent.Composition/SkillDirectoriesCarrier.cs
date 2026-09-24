using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container holder for the stored skill-directory lists. The factory
///     fills it after its preference read and before the container hands the session
///     out; the remote-host supervisor registration (AgentComposition) overlays the
///     values onto the settings it serializes - so the host receives the SAME
///     directory config the app-side session renders. Mutable by design: it is
///     construction-phase glue. <see cref="ResolvedSkillDirectories"/> carries the
///     parsed directories the container registered beside it.</summary>
public sealed class SkillDirectoriesCarrier
{
  public string? Global { get; private set; }

  public string? Workspace { get; private set; }

  public void Fill(string? global, string? workspace)
  {
    Global = global;
    Workspace = workspace;
  }
}

/// <summary>The parsed skill directories (both scopes merged, global entries before
///     workspace entries) registered per container: the input the composite skill
///     catalog consumes.</summary>
public sealed record ResolvedSkillDirectories(IReadOnlyList<SkillDirectory> List);
