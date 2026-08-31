using System.Text.Json;


namespace eThangAgent.AgentDomain.Tests;

public class SpawnContractTests
{
  [Fact]
  public void Defaults_AreUnboundedAndGrantless()
  {
    SpawnContract contract = new();
    Assert.Null(contract.ResultSchema);
    Assert.Null(contract.CapabilityGrants);
    Assert.Null(contract.Budgets);
    Assert.Equal(0, contract.MaxUrgency);
    Assert.False(contract.PreemptGrant); // approved D1
  }

  [Fact]
  public void RoundTrip_PreservesEveryMember()
  {
    SpawnContract contract = new(
        ResultSchema: """{ ""type"": ""object"" }""",
        CapabilityGrants: new Dictionary<string, string> { ["tool.allow"] = "web_fetch;exec" },
        Budgets: new BudgetCeilings(MaxTokens: 100_000, MaxCost: 1.5m, MaxToolCalls: 200),
        MaxUrgency: 2, PreemptGrant: true);
    string? encoded = SpawnContract.Encode(contract);
    Assert.NotNull(encoded);
    SpawnContract decoded = SpawnContract.Decode(encoded);
    Assert.Equal(contract.ResultSchema, decoded.ResultSchema);
    Assert.Equal(contract.MaxUrgency, decoded.MaxUrgency);
    Assert.Equal(contract.PreemptGrant, decoded.PreemptGrant);
    Assert.Equal(contract.Budgets, decoded.Budgets); // nested record (all scalars) is structural
    Assert.NotNull(contract.CapabilityGrants);
    Assert.NotNull(decoded.CapabilityGrants);
    Assert.Equal(contract.CapabilityGrants["tool.allow"], decoded.CapabilityGrants["tool.allow"]);
  }

  [Fact]
  public void Decode_RejectsGarbage()
      => Assert.Throws<JsonException>(() => SpawnContract.Decode("not json"));
}
