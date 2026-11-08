using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #374: a workspace opened with a trailing separator is the SAME workspace, and
///     the store boundary is where that is enforced. An id that is not normalized is REFUSED at
///     construction rather than accepted, because accepting it partitions one directory into two
///     workspaces - the defect this pins: a chain step session could not read the plan it was
///     executing, because the plan was written under the other spelling.
///     <para>Refusing is what makes the failure loud and the caller findable; the alternative
///     (normalizing quietly) would leave the caller writing rows under a spelling the store then
///     reads under another.</para></summary>
public sealed class WorkspaceIdentityBoundaryTests : IDisposable
{
  private const string Normalized = "C:\\ws\\alpha";
  private const string Respelled = "C:\\ws\\alpha\\";
  private readonly string _dbPath = TemplateDatabase.Copy();

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private AppDatabase Database() => new(_dbPath);

  [Fact]
  public void PlanStore_OfASecondSpelling_RefusesToConstruct()
  {
    ArgumentException refused = Assert.Throws<ArgumentException>(() => new SqlitePlanStore(Database(), Respelled));
    Assert.Contains(Normalized, refused.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ChainStore_OfASecondSpelling_RefusesToConstruct()
      => _ = Assert.Throws<ArgumentException>(() => new SqliteChainStore(Database(), Respelled));

  [Fact]
  public void PlanReviewStore_OfASecondSpelling_RefusesToConstruct()
      => _ = Assert.Throws<ArgumentException>(() => new SqlitePlanReviewStore(Database(), Respelled));

  [Fact]
  public void CommandRunStore_OfASecondSpelling_RefusesToConstruct()
      => _ = Assert.Throws<ArgumentException>(() => new SqliteCommandRunStore(Database(), Respelled));

  /// <summary>The state store takes its scope per call rather than at construction, so the
  ///     refusal lands on the call: a read under the second spelling would otherwise answer
  ///     'nothing here' for a key that exists.</summary>
  [Fact]
  public async Task StateStore_RefusesASecondSpellingOnEveryCall()
  {
    SqliteStateStore store = new(Database());
    CancellationToken ct = TestContext.Current.CancellationToken;
    _ = await store.SetKeyCasAsync(Normalized, "ns", "key", "value", null, ct);

    _ = await Assert.ThrowsAsync<ArgumentException>(() => store.GetKeyAsync(Respelled, "ns", "key", ct));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => store.ListKeysAsync(Respelled, null, ct));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => store.SetKeyCasAsync(Respelled, "ns", "key", "other", null, ct));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteKeyCasAsync(Respelled, "ns", "key", null, ct));
  }

  /// <summary>The half the refusal protects: the normalized spelling is the ONE spelling, so a
  ///     row written through one store is read by the next store opened on the same directory.
  ///     </summary>
  [Fact]
  public async Task PlanStore_OfTheNormalizedSpelling_ReadsWhatThePreviousOneWrote()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    SqlitePlanStore writer = new(Database(), Normalized);
    Result<Plan> created = await writer.CreateAsync(
        Plan.New("T", "G", "s", DateTimeOffset.UtcNow).AddStep("one", null), ct);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected success");

    SqlitePlanStore reader = new(Database(), Normalized);
    Result<Plan> back = await reader.GetAsync(created.Value.Id, ct);
    Assert.True(back.IsSuccess, back.Error?.Message ?? "expected the plan to be visible");
  }
}
