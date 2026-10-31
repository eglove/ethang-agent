namespace eThangAgent.ToolDomain.Tests;

/// <summary>Issue #304 D4: the universal floor is a NAMED CONSTANT, and its contents are
///     PINNED. Widening it is a product decision that edits this test in the same change, which
///     is exactly what a configurable floor would let somebody skip.</summary>
public class ChainFloorTests
{
  /// <summary>The exact set, in order. A new rule fails here until somebody states why no
  ///     passing test would license the act it refuses.</summary>
  [Fact]
  public void Rules_AreExactlyTheFourNamedActs() =>
      Assert.Equal(["force-push", "history-rewrite", "publish", "out-of-workspace-write"],
          ChainFloorRules.Ids);

  /// <summary>Every rule states WHY no gate licenses it. A rule with no reason is an entry
  ///     nobody can audit, and the refusal renders the reason verbatim.</summary>
  [Fact]
  public void EveryRule_CarriesAReasonAndAKind() =>
      Assert.All(ChainFloorRules.Rules, rule =>
      {
        Assert.False(string.IsNullOrWhiteSpace(rule.Why));
        Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        Assert.True(Enum.IsDefined(rule.Kind));
      });

  /// <summary>The workspace-escape entry is the ONE rule recognized from a resolved path rather
  ///     than from a command line, and its empty token list says so. Pinned because a token list
  ///     added there would be a second, worse implementation of a check the file tools own.
  ///     </summary>
  [Fact]
  public void OutOfWorkspaceRule_CarriesNoTokens()
  {
    ChainFloorRule rule = Assert.Single(ChainFloorRules.Rules,
        candidate => candidate.Kind == ChainFloorRuleKind.OutOfWorkspace);
    Assert.Empty(rule.Tokens);
  }

  /// <summary>Every other rule must be recognizable: a rule with tokens that match nothing is a
  ///     rule that never fires.</summary>
  [Fact]
  public void EveryRecognizableRule_HasAtLeastOneToken() =>
      Assert.All(
          ChainFloorRules.Rules.Where(rule => rule.Kind != ChainFloorRuleKind.OutOfWorkspace),
          rule => Assert.NotEmpty(rule.Tokens));

  [Theory]
  [InlineData("git push --force origin main", "force-push")]
  [InlineData("git push -f", "force-push")]
  [InlineData("git push origin +main", "force-push")]
  [InlineData("git push --force-with-lease", "force-push")]
  [InlineData("git commit --amend --no-edit", "history-rewrite")]
  [InlineData("git reset --hard HEAD~1", "history-rewrite")]
  [InlineData("gh release create v1.0", "publish")]
  [InlineData("npm publish --access public", "publish")]
  [InlineData("docker push registry/x:1", "publish")]
  public void TripFor_RecognizesEachSpellingOfAnAct(string command, string expectedRuleId) =>
      Assert.Equal(expectedRuleId, ChainFloorRules.TripFor(command)?.Id);

  /// <summary>CASE-INSENSITIVE, because a shell does not care about case and a check that did
  ///     would be defeated by one capital letter.</summary>
  [Fact]
  public void TripFor_IsCaseInsensitive() =>
      Assert.Equal("force-push", ChainFloorRules.TripFor("GIT PUSH --FORCE")?.Id);

  /// <summary>THE ORDINARY CASE: an act nobody put on the floor trips nothing. A floor that
  ///     refused everything would park every chain.</summary>
  [Theory]
  [InlineData("dotnet build")]
  [InlineData("git status --porcelain")]
  [InlineData("git push origin feature")]
  [InlineData("git commit -m \"feat: add a thing\"")]
  [InlineData("dotnet test eThangAgent.slnx --max-parallel-test-modules 4")]
  [InlineData("")]
  [InlineData("   ")]
  public void TripFor_LeavesOrdinaryCommandsAlone(string command) =>
      Assert.Null(ChainFloorRules.TripFor(command));

  /// <summary>Every refusal names BOTH exits (D3): the human proceeds, or the chain is
  ///     abandoned. A refusal with no exit makes the cheapest way past it a lie.</summary>
  [Fact]
  public void Refusal_NamesTheActBothExitsAndWhyNoGateLicensesIt()
  {
    ChainFloorRule rule = ChainFloorRules.Rules[0];
    string refusal = ChainFloorRules.Refusal(rule, "  git push --force  ");

    Assert.Contains("git push --force", refusal, StringComparison.Ordinal);
    Assert.Contains(rule.Id, refusal, StringComparison.Ordinal);
    Assert.Contains("No passing gate", refusal, StringComparison.Ordinal);
    Assert.Contains("PARKED", refusal, StringComparison.Ordinal);
    Assert.Contains("Abandoned", refusal, StringComparison.Ordinal);
  }

  /// <summary>The trip ledger records the act and returns the SAME sentence the model saw, so
  ///     the park names what was refused rather than a re-rendered guess.</summary>
  [Fact]
  public void Ledger_TripRecordsAndReturnsTheRefusal()
  {
    ChainFloorLedger ledger = new();
    ChainFloorRule rule = ChainFloorRules.Rules[0];

    string refusal = ledger.Trip(rule, "git push --force");

    Assert.True(ledger.Tripped);
    ChainFloorTrip trip = Assert.Single(ledger.Trips);
    Assert.Equal(rule.Id, trip.RuleId);
    Assert.Equal("git push --force", trip.Command);
    Assert.Equal(refusal, trip.Refusal);
    Assert.Equal(refusal, ledger.First?.Refusal);
  }

  /// <summary>THE FIRST TRIP IS THE ONE A PARK NAMES: a program that refuses three acts should
  ///     report the first, not the queue behind it.</summary>
  [Fact]
  public void Ledger_FirstIsTheFirstRefusedAct()
  {
    ChainFloorLedger ledger = new();
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");
    _ = ledger.Trip(ChainFloorRules.Rules[2], "npm publish");

    Assert.Equal(2, ledger.Trips.Count);
    Assert.Equal("force-push", ledger.First?.RuleId);
  }

  /// <summary>Reset is what keeps one turn's refusal from parking the NEXT turn.</summary>
  [Fact]
  public void Ledger_ResetForgetsEveryTrip()
  {
    ChainFloorLedger ledger = new();
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");

    ledger.Reset();

    Assert.False(ledger.Tripped);
    Assert.Empty(ledger.Trips);
    Assert.Null(ledger.First);
  }

  /// <summary>An untouched ledger is the ordinary case and must report it plainly.</summary>
  [Fact]
  public void Ledger_StartsEmpty()
  {
    ChainFloorLedger ledger = new();

    Assert.False(ledger.Tripped);
    Assert.Null(ledger.First);
    Assert.Empty(ledger.Trips);
  }
}
