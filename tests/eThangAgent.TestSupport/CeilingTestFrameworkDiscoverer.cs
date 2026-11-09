using Xunit.Sdk;
using Xunit.v3;

namespace eThangAgent.TestSupport;

/// <summary>xUnit's discoverer with one addition: every test case it reports is passed through
///     <see cref="CeilingTestCases.WithCeiling"/> first, so the ceiling reaches every test
///     without a single attribute changing.
///     <para>WHY THE DISCOVERY CALLBACK AND NOT THE DISCOVERER CACHE: xUnit builds a case from
///     the fact attribute's discoverer, and substituting that discoverer means seeding a cache
///     the framework owns and rebuilds. Wrapping the callback is the point where every case has
///     been built and none has been reported yet, so it covers facts, theories, and any future
///     case shape uniformly - including a test that names its own discoverer.</para>
///     </summary>
public sealed class CeilingTestFrameworkDiscoverer(
    IXunitTestAssembly testAssembly, IXunitTestCollectionFactory collectionFactory)
    : XunitTestFrameworkDiscoverer(testAssembly, collectionFactory)
{
  protected override ValueTask<bool> FindTestsForMethod(IXunitTestMethod testMethod,
      ITestFrameworkDiscoveryOptions discoveryOptions, Func<ITestCase, ValueTask<bool>> discoveryCallback)
      => base.FindTestsForMethod(testMethod, discoveryOptions, WithCeiling(discoveryCallback));

  protected override ValueTask<bool> FindTestsForType(IXunitTestClass testClass,
      ITestFrameworkDiscoveryOptions discoveryOptions, Func<ITestCase, ValueTask<bool>> discoveryCallback)
      => base.FindTestsForType(testClass, discoveryOptions, WithCeiling(discoveryCallback));

  /// <summary>The callback with the ceiling applied to each case it reports.</summary>
  private static Func<ITestCase, ValueTask<bool>> WithCeiling(Func<ITestCase, ValueTask<bool>> callback)
      => testCase => callback(testCase is IXunitTestCase xunit
          ? CeilingTestCases.WithCeiling(xunit)
          : testCase);
}
