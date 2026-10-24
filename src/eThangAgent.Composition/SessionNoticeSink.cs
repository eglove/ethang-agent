namespace eThangAgent.Composition;

/// <summary>Per-container carrier for the session's out-of-band notice sink
///     (issue #269). The sink belongs to the HOST UI - the view-model owns the
///     transcript - but background composition services (the skill hot-reload
///     announcement, the skill-invocation tool, the remote-host supervisor) need to
///     reach it from inside the container.
///     <para>Those services used to resolve <see cref="AgentSession"/> from DI, which is
///     registered NOWHERE: it is constructed directly by the factory and returned to the
///     caller. Every such resolution threw, and each caller swallowed the fault by its
///     best-effort contract, so the transcript notice silently never appeared. The
///     carrier is the resolvable seam: it exists from container build, the shell fills
///     <see cref="Sink"/> once it has a transcript, and a container nobody attached to
///     (a headless host) drops notices by contract instead of throwing.</para>
///     <para>Thread-safe by contract: notices are posted from background supervisory and
///     watcher paths.</para></summary>
public sealed class SessionNoticeSink
{
  /// <summary>The attached sink, or null when no UI has attached one yet. Volatile
  ///     semantics: written by the shell on the UI thread, read from background paths.</summary>
  public Action<string>? Sink { get; set; }

  /// <summary>Posts one notice, or drops it when nothing is attached (headless hosts).</summary>
  public void Post(string message)
  {
    ArgumentNullException.ThrowIfNull(message);
    Sink?.Invoke(message);
  }
}
