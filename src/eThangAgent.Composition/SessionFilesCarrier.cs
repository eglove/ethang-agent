namespace eThangAgent.Composition;

/// <summary>Per-container holder for the stored session-file lists (E). The factory
///     fills it after its preference read and before orphan repair; the remote-host
///     supervisor registration (resolved later, inside repair) overlays the values
///     onto the settings it serializes - so the host receives the SAME files the
///     app-side session renders. Mutable by design: it is construction-phase glue,</summary>
public sealed class SessionFilesCarrier
{
  public string? Global { get; private set; }

  public string? Workspace { get; private set; }

  public void Fill(string? global, string? workspace)
  {
    Global = global;
    Workspace = workspace;
  }
}
