namespace eThangAgent.TestSupport;

/// <summary>The only way a test in this repository is allowed not to run.
///     <para>A test that does not run is not a test: it reports green while asserting nothing,
///     and nobody notices when the thing it was written to prove breaks. So every test either
///     runs or is deleted. The one exception is a test that needs a resource the default run
///     cannot supply - a downloaded language server, a real network peer - which is opt-in: it
///     is skipped by default and runs when the named environment variable is set to 1.</para>
///     <para>An opt-in test carries <c>[Trait("Category", "OptIn")]</c> so it can be selected
///     as a group, and it skips through this method so the skip message always names the switch
///     that turns it on.</para></summary>
public static class OptInTest
{
  /// <summary>Skips the calling test unless <paramref name="variable"/> is set to "1", naming
  ///     the variable and what must be in place for the test to pass once it is.</summary>
  public static void SkipUnlessEnabled(string variable, string requirement)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(variable);
    ArgumentException.ThrowIfNullOrWhiteSpace(requirement);

    if (Environment.GetEnvironmentVariable(variable) is "1")
    {
      return;
    }

    Xunit.Assert.Skip("Opt-in: set " + variable + "=1 " + requirement);
  }
}
