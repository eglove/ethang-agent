using Xunit.v3;

namespace eThangAgent.TestSupport;

/// <summary>Applies the ceiling to a test case xUnit has already built.
///     <para>THE CASE IS REBUILT RATHER THAN MUTATED. <c>XunitTestCase.Timeout</c> is set from
///     the fact attribute at construction and its setter is not public, so the bound is applied
///     by constructing the case again with the same identity xUnit gave it - same display name,
///     same unique id, same traits, same arguments - and the ceiling in place of the timeout.
///     Rebuilding keeps every other property exactly as xUnit decided it.</para>
///     <para>WHAT IS LEFT ALONE: a case that already carries a timeout keeps it, because a test
///     that states its own bound has said something the ceiling must not overwrite; a
///     <c>LatencyContract</c> test is exempt, because its subject is the duration and a bound
///     would be the thing under test rather than a guard around it; and any case type this
///     builder does not know how to reconstruct is returned untouched rather than approximated,
///     so an unfamiliar case is never silently altered.</para>
///     <para>A CEILING OF ZERO MEANS NO BOUND. That happens only when the configured ceiling is
///     too large to express in whole milliseconds, and inventing a different number there would
///     be worse than leaving the test unbounded.</para></summary>
public static class CeilingTestCases
{
  /// <summary><paramref name="testCase"/> with the ceiling applied, or unchanged when it keeps
  ///     its own bound, is exempt, or is a shape this builder does not reconstruct.</summary>
  public static IXunitTestCase WithCeiling(IXunitTestCase testCase)
  {
    ArgumentNullException.ThrowIfNull(testCase);
    if (TestCeiling.TestBudgetMilliseconds == 0 || IsExempt(testCase))
    {
      return testCase;
    }

    // ONLY THE EXACT BASE SHAPE IS REBUILT. A subclass carries behaviour the constructor does
    // not: AvaloniaFact's case runs the body on the UI thread, and rebuilding it as a plain
    // XunitTestCase would silently drop that - the test would neither run nor fail. An
    // unrecognized subclass is therefore left exactly as xUnit built it.
    int ceiling = TestCeiling.TestBudgetMilliseconds;
    return testCase is XunitTestCase xunit && testCase.GetType() == typeof(XunitTestCase)
        ? new XunitTestCase(xunit.TestMethod, xunit.TestCaseDisplayName, xunit.UniqueID,
            xunit.Explicit, xunit.SkipExceptions, xunit.SkipReason, xunit.SkipType,
            xunit.SkipUnless, xunit.SkipWhen, xunit.Traits, xunit.TestMethodArguments,
            xunit.SourceFilePath, xunit.SourceLineNumber, ceiling)
        : testCase;
  }

  /// <summary>True when the case states its own bound or declares itself a latency contract.
  ///     Both are decisions the test made about itself, so both are respected.
  ///     <para>TRAITS ARE KEYED BY NAME, NOT BY VALUE. A <c>[Trait("Category", "LatencyContract")]</c>
  ///     arrives here as the pair <c>Category</c> to <c>LatencyContract</c>, so the contract is
  ///     found by reading the values of the <c>Category</c> trait - looking the value up as a key
  ///     finds nothing and silently exempts no test at all.</para></summary>
  private static bool IsExempt(IXunitTestCase testCase)
      => testCase.Timeout != 0
          || (testCase.Traits.TryGetValue(CeilingTestFramework.CategoryTrait,
                  out IReadOnlyCollection<string>? categories)
              && categories.Contains(CeilingTestFramework.LatencyContractTrait, StringComparer.Ordinal));
}
