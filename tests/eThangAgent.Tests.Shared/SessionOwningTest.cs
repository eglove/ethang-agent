using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Xunit;

namespace eThangAgent.TestSupport;

/// <summary>Base class for a test that opens agent sessions: it owns their disposal and
///     performs it from <see cref="DisposeAsync"/>, so a FAILING ASSERTION CANNOT SKIP IT
///     (issue #284 item 3).
///     <para>The defect: the established pattern disposed the session in a `finally` inside
///     the test body. Disposal as the last statement of the try block is simply skipped
///     when an assertion throws above it - and a leaked session is not an inert object.
///     It owns a real eThangAgent.ChildHost process, which holds its SQLite file and
///     silently slows every later run (measured during #271: the composition suite took
///     over 4 minutes with a leaked host alive and 1m15s once the orphan was killed, with
///     no test failing and no other symptom).</para>
///     <para>xUnit runs IAsyncLifetime.DisposeAsync regardless of test outcome, which is
///     what makes this the correct home for teardown. Sessions are tracked by reference,
///     and a session whose start FAILED is never registered - the factory already released
///     its container, so there is nothing to dispose.</para>
///     <para>Usage: derive from this class and call <see cref="CreateSessionAsync"/>. It
///     also owns the temp database and workspace, so a test need not repeat that cleanup.</para></summary>
public abstract class SessionOwningTest : IAsyncLifetime
{
  private readonly List<AgentSession> _sessions = [];
  private readonly List<string> _tempPaths = [];

  /// <summary>Creates a session rooted at a fresh temp workspace over a fresh temp
  ///     database, and registers it for disposal. Throws when creation fails, so a test
  ///     never continues against a null session: a failed create is a real failure the test
  ///     should report at its own assertion, not a NullReferenceException three lines later.
  ///     <para>The temp database is selected through ETHANG_AGENT_DB exactly as production
  ///     selects it; teardown restores it.</para></summary>
  protected async Task<AgentSession> CreateSessionAsync(AgentSettings settings, string? workspaceRoot = null)
  {
    ArgumentNullException.ThrowIfNull(settings);

    string dbPath = Track(Path.Combine(Path.GetTempPath(),
        "ethang-session-" + Guid.NewGuid().ToString("N") + ".db"));
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);

    string root = workspaceRoot ?? Track(Directory.CreateTempSubdirectory("ethang-ws").FullName);
    AgentSessionFactory factory = new(settings, new AppDatabase(dbPath));
    Result<AgentSession> created = await factory.CreateAsync(root, Providers.OpenRouter,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    if (!created.IsSuccess)
    {
      throw new InvalidOperationException(
          $"session creation failed: [{created.Error.Code}] {created.Error.Message}");
    }

    _sessions.Add(created.Value);
    return created.Value;
  }

  /// <summary>Registers an already-created session for disposal - for tests that drive
  ///     the factory directly (resume paths, anchor validation, named-failure contracts).</summary>
  protected void TrackSession(Result<AgentSession> session)
  {
    if (session is { IsSuccess: true })
    {
      _sessions.Add(session.Value);
    }
  }

  /// <summary>Registers a temp file or directory the test needs, deleted by teardown.
  ///     One method for both: the check is exists-as-directory, so the caller need not say
  ///     which it is.</summary>
  protected string Track(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    _tempPaths.Add(path);
    return path;
  }

  public ValueTask InitializeAsync() => ValueTask.CompletedTask;

  /// <summary>Disposes every tracked session, then deletes the tracked temp paths - even
  ///     when a test threw, which is the whole point of putting it here. Temp deletion is
  ///     best effort: a file briefly held by a dying host must not turn a green test red.</summary>
  public async ValueTask DisposeAsync()
  {
    foreach (AgentSession session in _sessions)
    {
      await session.Services.DisposeAsync().ConfigureAwait(false);
    }

    _sessions.Clear();
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);

    foreach (string path in _tempPaths)
    {
      DeleteQuietly(path);
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>Best-effort temp cleanup; see the named decision on the caller.</summary>
  private static void DeleteQuietly(string path)
  {
    // Named decision (CA1031): a temp file still held by a dying host is not a test
    // failure, and throwing here would mask the real one.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      if (Directory.Exists(path))
      {
        Directory.Delete(path, recursive: true);
      }
      else if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch
    {
      // Deliberately ignored: temp cleanup is not part of any test's contract.
    }
#pragma warning restore CA1031
  }
}
