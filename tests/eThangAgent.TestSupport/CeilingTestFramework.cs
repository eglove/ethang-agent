using System.Reflection;
using Xunit.v3;

namespace eThangAgent.TestSupport;

/// <summary>The test framework every suite in this repository runs on: xUnit's own, with one
///     addition - every test carries <see cref="TestCeiling.Value"/> as its timeout.
///     <para>WHY THIS EXISTS. Without a per-test bound a test that awaits something that never
///     settles does not fail, it HANGS: the module stops reporting, the run has no output to
///     read, and the only evidence is a wall clock. The failure then costs whatever budget the
///     operator was willing to wait, and the orphaned test host has to be cleaned up before
///     anything can be rerun. With the bound, the same mistake is a named failing test with a
///     stack trace showing where it was waiting.</para>
///     <para>THE BOUND IS THE CEILING, NOT A NEW NUMBER. <see cref="TestCeiling.Value"/> is
///     already the one wall-clock bound a test may hold and is already set high enough that a
///     loaded machine cannot reach it, so applying it automatically adds no second definition of
///     'too slow' that could drift from the first.</para>
///     <para>OPTING OUT IS EXPLICIT AND AUDITABLE. A test that declares its own timeout keeps
///     it, and a <c>LatencyContract</c> test is exempt because its subject IS the duration. Both
///     are properties of the test, so the exemption is visible where the test is written rather
///     than in a list somewhere else.</para>
///     <para>REGISTERED BY <c>tests/Directory.Build.props</c> for every test project, so a new
///     project cannot join the suite without the bound.</para></summary>
public sealed class CeilingTestFramework : XunitTestFramework
{
  /// <summary>The trait name every categorized test carries.</summary>
  public const string CategoryTrait = "Category";

  /// <summary>The category that marks a test whose subject is a duration, and which therefore
  ///     keeps whatever bound it declares rather than the ceiling.</summary>
  public const string LatencyContractTrait = "LatencyContract";

  public CeilingTestFramework() : base("xunit.runner.json")
  {
  }

  /// <summary>The stock discoverer, re-wrapped in the ceiling's own.
  ///     <para>THE STOCK ONE IS BUILT FIRST AND ITS PARTS ARE REUSED, rather than constructing a
  ///     test assembly and collection factory here. Those two carry behaviour a hand-built copy
  ///     does not: the collection factory decides which tests may run in parallel, and a copy
  ///     that guesses wrong changes the timing every test in the suite runs under. Asking the
  ///     framework for its own discoverer keeps every decision xUnit made and changes only the
  ///     one thing this class exists to change.</para></summary>
  protected override ITestFrameworkDiscoverer CreateDiscoverer(Assembly assembly)
  {
    ArgumentNullException.ThrowIfNull(assembly);
    XunitTestFrameworkDiscoverer stock = (XunitTestFrameworkDiscoverer)base.CreateDiscoverer(assembly);
    return new CeilingTestFrameworkDiscoverer(stock.TestAssembly, stock.TestCollectionFactory);
  }
}
