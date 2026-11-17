namespace eThangAgent.Composition;

/// <summary>Per-container holder for the stored ticked-tool list (plan #209), the same
///     construction-phase glue <see cref="TrustedDirectoriesCarrier" /> is: the factory fills
///     it after its preference read and the RemoteHost block overlays the value onto the
///     settings JSON the host receives - so the host process, which builds the prompt for
///     every production turn, announces exactly the tools the app-side session does.
///     <para>Mutable by design: it is construction-phase glue, filled once before the
///     container hands the session out.</para></summary>
public sealed class PathToolsCarrier
{
  public string? Stored { get; private set; }

  public void Fill(string? stored) => Stored = stored;
}
