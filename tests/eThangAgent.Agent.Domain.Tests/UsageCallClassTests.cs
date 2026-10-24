using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

// B10 (issue #22): the call class is a closed set validated at construction, and B-legacy:
// a null class reads as main_loop because every row written before the class existed came
// from the loop.
public class UsageCallClassTests
{
  [Fact]
  public void FromWire_KnownNames_ResolveTheClass()
  {
    Assert.Same(UsageCallClass.MainLoop,
        UsageCallClass.FromWire("main_loop").Value);
    Assert.Same(UsageCallClass.Compaction,
        UsageCallClass.FromWire("compaction").Value);
  }

  [Fact]
  public void FromWire_NullName_ReadsAsMainLoop()
  {
    // The legacy rule: a row written before the class existed came from the loop, so null
    // has exactly one correct meaning.
    Result<UsageCallClass> parsed = UsageCallClass.FromWire(null);

    Assert.True(parsed.IsSuccess);
    Assert.Same(UsageCallClass.MainLoop, parsed.Value);
  }

  [Theory]
  [InlineData("model_selection")]
  [InlineData("MainLoop")]
  [InlineData("")]
  [InlineData("unknown")]
  public void FromWire_UnknownName_FailsWithDomainError(string wireName)
  {
    Result<UsageCallClass> parsed = UsageCallClass.FromWire(wireName);

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidCallClass", parsed.Error.Code);
  }

  [Fact]
  public void Serialize_WritesTheBareWireName()
  {
    Assert.Equal("\"main_loop\"", JsonSerializer.Serialize(UsageCallClass.MainLoop));
    Assert.Equal("\"compaction\"", JsonSerializer.Serialize(UsageCallClass.Compaction));
  }

  [Fact]
  public void Deserialize_KnownName_RoundTrips()
      => Assert.Same(UsageCallClass.Compaction,
          JsonSerializer.Deserialize<UsageCallClass>("\"compaction\""));

  [Fact]
  // Corrupt persisted data is an infrastructure fault, never a silent coercion.
  public void Deserialize_UnknownName_ThrowsJsonException()
      => _ = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UsageCallClass>("\"nope\""));

  [Fact]
  public void EffectiveCallClass_NullClass_ReadsAsMainLoop()
  {
    AgentUsageRecorded legacy = new(AgentId.NewId(), DateTimeOffset.UtcNow, Guid.NewGuid(), 0, 10, 2, null, null);

    Assert.Null(legacy.CallClass);
    Assert.Same(UsageCallClass.MainLoop, legacy.EffectiveCallClass);
  }

  [Fact]
  public void EffectiveCallClass_ExplicitClass_IsPreserved()
  {
    AgentUsageRecorded row = new(AgentId.NewId(), DateTimeOffset.UtcNow, Guid.NewGuid(), 0, 10, 2, null, null,
        UsageCallClass.Compaction);

    Assert.Same(UsageCallClass.Compaction, row.EffectiveCallClass);
  }
}
