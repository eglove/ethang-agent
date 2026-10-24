namespace eThangAgent.Storage.ACL;

/// <summary>Cross-process change detection for the app database (issue #8). The
///     conversation runs in a ChildHost process, so a plan or todo the agent writes is
///     written by ANOTHER process: no in-process bus can see it. SQLite's
///     <c>PRAGMA data_version</c> is the cheap signal - one integer, no query plan - and
///     it bumps only on OTHER connections' commits, deliberately ignoring this one's own
///     writes (verified: a reader's own insert leaves it unchanged).
///     <para>A view polls <see cref="Read"/> at its own cadence and re-reads when the
///     value moves. The interface lives here so the UI never knows SQL exists.</para></summary>
public interface IChangeSignal
{
  /// <summary>The current data version. Changes only when another connection commits.</summary>
  long Read();
}
