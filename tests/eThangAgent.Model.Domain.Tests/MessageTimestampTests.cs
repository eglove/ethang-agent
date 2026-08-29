using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class MessageTimestampTests
{
  [Fact]
  public void Stamp_NonUtcOffset_RendersNormalizedUtcInContractFormat()
  {
    Message message = new(Role.User, "review my diff",
        new DateTimeOffset(2026, 1, 15, 10, 30, 5, TimeSpan.FromHours(2)));

    string stamped = MessageTimestamp.Stamp(message);

    Assert.Equal("[2026-01-15 08:30:05Z] review my diff", stamped);
  }

  [Fact]
  public void Stamp_IsDeterministic_ForRepeatedCalls()
  {
    Message message = new(Role.Assistant, "done",
        new DateTimeOffset(2026, 1, 15, 8, 30, 5, TimeSpan.Zero));

    Assert.Equal(MessageTimestamp.Stamp(message), MessageTimestamp.Stamp(message));
  }

  [Fact]
  public void Stamp_EmptyContent_KeepsTheUniformPrefixFormat()
  {
    Message message = new(Role.Assistant, "", new DateTimeOffset(2026, 1, 15, 8, 30, 5, TimeSpan.Zero));

    string stamped = MessageTimestamp.Stamp(message);

    Assert.Equal("[2026-01-15 08:30:05Z] ", stamped);
  }
}
