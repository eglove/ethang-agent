using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless UI-level guard for stream coalescing: with the production marshaled sink,
/// a rapid delta burst must produce bounded transcript Replace notifications (one per
/// window, not one per token), keep the tail in view while stuck, and still deliver
/// the full text to the transcript.
/// </summary>
public class StreamCoalescingHeadlessTests
{
  /// <summary>Counts transcript mutations by action kind, by view wiring.</summary>
  private sealed class ReplaceCounter
  {
    public int Adds { get; private set; }
    public int Replaces { get; private set; }

    public void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
      if (e.Action == NotifyCollectionChangedAction.Add)
      {
        Adds++;
      }
      else if (e.Action == NotifyCollectionChangedAction.Replace)
      {
        Replaces++;
      }
    }
  }

  /// <summary>Runner streaming <paramref name="count"/> rapid content deltas.
  ///     The VM wraps every runner in OffUiThread, so the burst runs on a worker
  ///     thread - the production shape.</summary>
  private static TurnRunner BurstRunner(int count)
  {
#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
    Task<Result<string>> Runner(SendMessageCommand _c, CancellationToken _ct, TurnCallbacks? callbacks, Action<string>? _n = null)
    {
      if (callbacks?.OnContentDelta is { } delta)
      {
        for (int i = 0; i < count; i++)
        {
          delta("tok ");
        }
      }

      return Task.FromResult(Result.Success("burst done"));
    }
#pragma warning restore IDE0060, S1172

    return Runner;
  }

  [AvaloniaFact]
  public async Task Delta_Burst_Produces_Bounded_Replaces_And_Keeps_Tail_In_View()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(BurstRunner(400), marshalToUIThread: true);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    ScrollViewer scroll = view.GetControl<ScrollViewer>("TranscriptScroll");

    ReplaceCounter counter = new();
    vm.Transcript.Entries.CollectionChanged += counter.OnCollectionChanged;

    await vm.SubmitAsync("q").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    // Coalesced: per-window replaces, not per-token (threshold argument identical to
    // the integration test - slow CI stalls create more windows, still far below 400).
    Assert.True(counter.Replaces <= 40,
        $"expected bounded replaces, got {counter.Replaces} (per-token regression?)");
    Assert.True(counter.Adds >= 2, "user + assistant entries expected");

    string text = string.Concat(vm.Transcript.Entries
        .OfType<AssistantTextEntry>()
        .Select(e => e.Text));
    Assert.Equal(400 * "tok ".Length, text.Length);

    // While stuck (default), the tail stays in view after the stream settles.
    Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2.0,
        "tail must remain in view");
  }
}
