using Xunit;

namespace eThangAgent.TestSupport;

/// <summary>The assembly-level sweep that turns 'a run left debris in the temp directory' into a
///     NAMED failure (issue #371).
///     <para>The defect it exists for: every test created its database and workspace in the
///     operating system's temp directory and deleted it in its own teardown, with the deletion
///     wrapped in a bare catch. A test whose cleanup failed reported green, and nothing ever
///     swept what the swallowed failures left behind - measured at 242,000 files and 58 GB on
///     one machine.</para>
///     <para>Register it for every test assembly:
///     <c>[assembly: AssemblyFixture(typeof(TestTempRootFixture))]</c>. The fixture creates the
///     run's root before the first test and removes it after the last, and a root it cannot
///     remove fails the assembly rather than only the log.</para></summary>
public sealed class TestTempRootFixture : IAsyncLifetime
{
  /// <summary>Creates the run's root up front, so a run that writes nothing still owns a
  ///     directory whose absence is meaningful.</summary>
  public ValueTask InitializeAsync()
  {
    _ = TestTempRoot.Root;
    return ValueTask.CompletedTask;
  }

  /// <summary>Removes the run's root wholesale. A failure here is an assembly-level failure,
  ///     which xUnit reports to the operator.</summary>
  public ValueTask DisposeAsync()
  {
    TestTempRoot.Delete();
    return ValueTask.CompletedTask;
  }
}
