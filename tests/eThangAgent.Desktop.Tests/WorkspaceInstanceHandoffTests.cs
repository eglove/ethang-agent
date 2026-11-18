using eThangAgent.Windows.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>One process per workspace: a second launch for an owned directory signals the
///     owner and reports that it handed off, and two different directories never contend.
///     </summary>
public sealed class WorkspaceInstanceHandoffTests
{
  [Fact]
  public void InstanceName_IsStableAndDistinctPerRoot()
  {
    string alpha = WorkspaceInstanceHandoff.InstanceName("C:\\work\\alpha");
    string alphaAgain = WorkspaceInstanceHandoff.InstanceName("c:\\work\\alpha\\");
    string beta = WorkspaceInstanceHandoff.InstanceName("C:\\work\\beta");

    Assert.Equal(alpha, alphaAgain);
    Assert.NotEqual(alpha, beta);
  }

  [Fact]
  public void TrySignalExisting_ReturnsFalseWhenNothingOwnsTheName()
  {
    string root = UniqueRoot();

    Assert.False(WorkspaceInstanceHandoff.TrySignalExisting(root));
  }

  [Fact]
  public void Acquire_ThenSignal_DeliversTheRootToTheOwner()
  {
    string root = UniqueRoot();
    using ManualResetEventSlim delivered = new();
    string? received = null;
    using SingleInstanceGate? gate = WorkspaceInstanceHandoff.Acquire(root, payload =>
    {
      received = payload;
      delivered.Set();
    });
    Assert.NotNull(gate);

    Assert.True(WorkspaceInstanceHandoff.TrySignalExisting(root));

    Assert.True(delivered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
        "the owner never received the signal");
    Assert.Equal(root, received);
  }

  [Fact]
  public void Acquire_ReturnsNullWhileAnotherOwnerHoldsTheName()
  {
    string root = UniqueRoot();
    using SingleInstanceGate? first = WorkspaceInstanceHandoff.Acquire(root, _ => { });
    Assert.NotNull(first);

    SingleInstanceGate? second = WorkspaceInstanceHandoff.Acquire(root, _ => { });

    Assert.Null(second);
  }

  [Fact]
  public void Acquire_DoesNotContendAcrossDifferentRoots()
  {
    using SingleInstanceGate? alpha = WorkspaceInstanceHandoff.Acquire(UniqueRoot(), _ => { });
    using SingleInstanceGate? beta = WorkspaceInstanceHandoff.Acquire(UniqueRoot(), _ => { });

    Assert.NotNull(alpha);
    Assert.NotNull(beta);
  }

  [Fact]
  public void Gate_IsReleasedOnDispose()
  {
    string root = UniqueRoot();
    SingleInstanceGate? first = WorkspaceInstanceHandoff.Acquire(root, _ => { });
    Assert.NotNull(first);
    first.Dispose();

    using SingleInstanceGate? second = WorkspaceInstanceHandoff.Acquire(root, _ => { });

    Assert.NotNull(second);
  }

  private static string UniqueRoot() =>
      "C:\\work\\" + Guid.NewGuid().ToString("N");
}
