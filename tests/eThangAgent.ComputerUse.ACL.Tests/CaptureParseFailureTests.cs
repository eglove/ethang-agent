using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>I10 pin: a capture_app reply that fails shape parsing produces a typed retryable
///     STALE_STATE failure - never an empty successful observation.</summary>
public class CaptureParseFailureTests
{
  [Fact]
  public async Task UnparseableCapture_YieldsTypedStaleStateFailure_NotEmptyObservation()
  {
    BrokerSupervisor supervisor = new("C:\\no\\such\\host.exe", "ethang-parse-test", "t");
    try
    {
      BrokerComputerAccess access = new(supervisor);
      await using (access.ConfigureAwait(true))
      {
        BrokerReply reply = new(
            Id: 9,
            Result: System.Text.Json.JsonSerializer.SerializeToElement(new { unexpected = true }),
            Error: null);

        ComputerOutcome outcome = access.CaptureToObservation(reply);

        ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(outcome);
        Assert.Equal(ComputerErrorCodes.StaleState, failure.Code);
        Assert.Contains("observe again", failure.Message, StringComparison.Ordinal);
      }
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
