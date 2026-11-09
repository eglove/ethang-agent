using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace eThangAgent.Analyzers.Tests;

/// <summary>Issue #378: the ceiling rule catches the SHAPE of a load-sensitive wait, not one
///     spelling of it. Each test plants one shape and asks the analyzer what it saw.</summary>
public sealed class TimedWaitAnalyzerTests
{
  [Fact]
  public async Task Loop_AgainstClockDeadlineBuiltFromALiteral_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          System.DateTime deadline = System.DateTime.UtcNow + System.TimeSpan.FromSeconds(5);
          while (System.DateTime.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Contains("deadline", AnalyzerHarness.Describe(diagnostics, TimedWaitAnalyzer.ClockBoundId), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Loop_AgainstCeilingDeadline_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          System.DateTime deadline = System.DateTime.UtcNow + eThangAgent.TestSupport.TestCeiling.Value;
          while (System.DateTime.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Loop_AgainstCeilingHeldInAField_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        private static readonly System.TimeSpan Limit = eThangAgent.TestSupport.TestCeiling.Value;

        public void Wait()
        {
          System.DateTimeOffset deadline = System.DateTimeOffset.UtcNow + Limit;
          while (System.DateTimeOffset.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Loop_AgainstCeilingPassedAsAParameter_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public void Wait() => WaitUntil(eThangAgent.TestSupport.TestCeiling.Value);

        private static void WaitUntil(System.TimeSpan limit)
        {
          System.DateTimeOffset deadline = System.DateTimeOffset.UtcNow + limit;
          while (System.DateTimeOffset.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Loop_WhoseHelperIsCalledWithALiteral_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait() => WaitUntil(System.TimeSpan.FromSeconds(5));

        private static void WaitUntil(System.TimeSpan limit)
        {
          System.DateTimeOffset deadline = System.DateTimeOffset.UtcNow + limit;
          while (System.DateTimeOffset.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.NotEmpty(diagnostics);
  }

  [Fact]
  public async Task Loop_GuardedByAClockBreakInsteadOfACondition_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
          while (true)
          {
            if (clock.Elapsed >= System.TimeSpan.FromSeconds(10))
            {
              break;
            }
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.NotEmpty(diagnostics);
  }

  [Fact]
  public async Task Race_AgainstALiteralDelay_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public async System.Threading.Tasks.Task Wait()
        {
          System.Threading.Tasks.Task work = System.Threading.Tasks.Task.CompletedTask;
          _ = await System.Threading.Tasks.Task.WhenAny(
              work, System.Threading.Tasks.Task.Delay(300));
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.NotEmpty(diagnostics);
  }

  [Fact]
  public async Task Race_AgainstACeilingDelay_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public async System.Threading.Tasks.Task Wait()
        {
          System.Threading.Tasks.Task work = System.Threading.Tasks.Task.CompletedTask;
          _ = await System.Threading.Tasks.Task.WhenAny(
              work, System.Threading.Tasks.Task.Delay(eThangAgent.TestSupport.TestCeiling.Value));
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Race_WhoseDelayIsTheNegativeArm_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public async System.Threading.Tasks.Task Wait()
        {
          System.Threading.Tasks.Task work = System.Threading.Tasks.Task.CompletedTask;
          System.Threading.Tasks.Task finished = await System.Threading.Tasks.Task.WhenAny(
              work, System.Threading.Tasks.Task.Delay(300));
          Assert.NotSame(work, finished);
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task CancellationTimeout_CancelAfterALiteral_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait(System.Threading.CancellationTokenSource cts)
          => cts.CancelAfter(System.TimeSpan.FromSeconds(1));
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    _ = Assert.Single(diagnostics);
  }

  [Fact]
  public async Task CancellationTimeout_BuiltFromALiteral_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          using System.Threading.CancellationTokenSource cts = new(System.TimeSpan.FromMilliseconds(300));
          _ = cts;
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    _ = Assert.Single(diagnostics);
  }

  [Fact]
  public async Task CancellationTimeout_BuiltFromTheCeiling_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          using System.Threading.CancellationTokenSource cts = new(eThangAgent.TestSupport.TestCeiling.Value);
          cts.CancelAfter(eThangAgent.TestSupport.TestCeiling.Value);
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Sleep_OutsideAnyLoop_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait() => System.Threading.Thread.Sleep(700);
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Contains("700", AnalyzerHarness.Describe(diagnostics, TimedWaitAnalyzer.FixedWaitId), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Sleep_PacingACeilingBoundedLoop_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          System.DateTimeOffset deadline = System.DateTimeOffset.UtcNow + eThangAgent.TestSupport.TestCeiling.Value;
          while (System.DateTimeOffset.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Sleep_InAHelperCalledOnlyFromACeilingBoundedLoop_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public void Wait()
        {
          System.DateTimeOffset deadline = System.DateTimeOffset.UtcNow + eThangAgent.TestSupport.TestCeiling.Value;
          while (System.DateTimeOffset.UtcNow < deadline)
          {
            Retry();
          }
        }

        private static void Retry() => System.Threading.Thread.Sleep(50);
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }

  [Fact]
  public async Task Sleep_InAHelperCalledOutsideAnyLoop_IsReported()
  {
    const string Source = """
      public class Probe
      {
        public void Wait() => Retry();

        private static void Retry() => System.Threading.Thread.Sleep(50);
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.NotEmpty(diagnostics);
  }

  [Fact]
  public async Task LatencyContractTrait_ExemptsTheClass()
  {
    const string Source = """
      [Xunit.Trait("Category", "LatencyContract")]
      public class Probe
      {
        public void Wait()
        {
          System.DateTime deadline = System.DateTime.UtcNow + System.TimeSpan.FromSeconds(5);
          while (System.DateTime.UtcNow < deadline)
          {
            System.Threading.Thread.Sleep(10);
          }
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.NotEmpty(diagnostics);
  }

  [Fact]
  public async Task ClockUsedForADiagnosticMessage_IsSilent()
  {
    const string Source = """
      public class Probe
      {
        public string Describe()
        {
          System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
          return "took " + clock.ElapsedMilliseconds + "ms";
        }
      }
      """;

    ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.DiagnoseAsync(Source);
    Assert.Empty(diagnostics);
  }
}
