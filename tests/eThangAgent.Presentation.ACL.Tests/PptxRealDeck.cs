using eThangAgent.TestSupport;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The two decks on this machine that are verification inputs rather than fixtures:
///     the 41-slide teaching deck belonging to another project and the small sample deck.
///     Neither is committed and neither is copied into the workspace. A test that needs one
///     is opt-in, and once opted in a missing deck is a failure rather than a silent pass -
///     which is the difference between a test that proves something and one that reports green
///     while asserting nothing.</summary>
internal static class PptxRealDeck
{
  internal const string TeachingDeckPath =
      @"C:\Users\glove\projects\lakewood\intro-to-computers\A+  220-1201 Module 1.pptx";

  internal const string SampleDeckPath =
      @"C:\Users\glove\go\pkg\mod\github.com\wailsapp\mimetype@v1.4.1\testdata\pptx.pptx";

  internal const string OptInVariable = "ETHANG_PPTX_REAL_DECKS";

  /// <summary>Skips unless the opt-in variable is set, then requires the teaching deck to
  ///     exist: past the opt-in a missing deck is a failure, not another skip.</summary>
  internal static void Teaching()
  {
    OptInTest.SkipUnlessEnabled(OptInVariable, "with the teaching deck at " + TeachingDeckPath + ".");
    Assert.True(File.Exists(TeachingDeckPath), "the teaching deck is missing from " + TeachingDeckPath);
  }

  /// <summary>Skips unless the opt-in variable is set, then requires the sample deck to
  ///     exist: past the opt-in a missing deck is a failure, not another skip.</summary>
  internal static void Sample()
  {
    OptInTest.SkipUnlessEnabled(OptInVariable, "with the sample deck at " + SampleDeckPath + ".");
    Assert.True(File.Exists(SampleDeckPath), "the sample deck is missing from " + SampleDeckPath);
  }
}
