using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>A view detached from the tree must be COLLECTIBLE even while its view-model is
///     still alive (issue #382).
///     <para>The view-model outlives the views that render it, and every subscription the
///     view takes points FROM the view-model TO the view. Nothing released them on the way
///     out, so the view-model's own event list kept each detached view - and the whole
///     visual tree beneath it - reachable for the life of the session. Measured before the
///     fix: five views bound to one live view-model and detached again were ALL still alive
///     after collection, against one of five unbound views.</para>
///     <para>These assert the CONTRACT - a detached view does not accumulate - rather than an
///     exact count, because a collection is not a deterministic event: a small residue can
///     survive one pass and be gone on the next.</para></summary>
[Trait("Category", "Process")]
public class AgentViewRetentionTests
{
  private const int Views = 20;

  /// <summary>The regression: detaching views from a live view-model must not retain them.</summary>
  [AvaloniaFact]
  public void A_Detached_View_Is_Collectible_While_Its_View_Model_Lives()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    List<WeakReference> views = [];
    Window window = new() { Width = 900, Height = 600 };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    for (int i = 0; i < Views; i++)
    {
      AgentView view = new() { DataContext = vm };
      window.Content = view;
      Dispatcher.UIThread.RunJobs();
      views.Add(new WeakReference(view));
      window.Content = null;
      Dispatcher.UIThread.RunJobs();
    }

    window.Close();
    int alive = LiveAfterCollection(views);

    // The view-model is deliberately still reachable here: that is the condition the leak
    // needed. A quarter is a wide margin over the observed residue and far below the leak,
    // which retained every single view.
    Assert.True(alive < Views / 4,
        $"{alive} of {Views} detached views are still alive while their view-model lives; " +
        "a detached view must not stay reachable through the view-model it was bound to");
    GC.KeepAlive(vm);
  }

  /// <summary>The other half of the contract: a view REMOVED from the tree and then
  ///     re-attached must still work. Releasing subscriptions on detach is only correct if
  ///     attaching restores them, so this drives the transcript through both states.</summary>
  [AvaloniaFact]
  public void A_Re_Attached_View_Still_Follows_Its_View_Model()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = 900, Height = 600 };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentView view = new() { DataContext = vm };
    window.Content = view;
    Dispatcher.UIThread.RunJobs();

    // Detach and re-attach: the wiring must come back with the view.
    window.Content = null;
    Dispatcher.UIThread.RunJobs();
    window.Content = view;
    Dispatcher.UIThread.RunJobs();

    vm.Transcript.AddNotice("after re-attach");
    Dispatcher.UIThread.RunJobs();

    ScrollViewer transcript = view.GetControl<ScrollViewer>("TranscriptScroll");
    Assert.True(transcript.Extent.Height > 0, "a re-attached view must lay its transcript out");
    Assert.Contains(vm.Transcript.Notices, n => n.Contains("after re-attach", StringComparison.Ordinal));
    window.Close();
  }

  /// <summary>How many of the referenced views survive a forced reclaim. Collected in
  ///     rounds because unload and finalization are asynchronous: a single pass can leave a
  ///     residue that the next one takes.</summary>
  private static int LiveAfterCollection(List<WeakReference> views)
  {
    int alive = views.Count;
    for (int attempt = 0; attempt < 12; attempt++)
    {
      Dispatcher.UIThread.RunJobs();
#pragma warning disable S1215 // Do not force garbage collection
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      GC.WaitForPendingFinalizers();
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore S1215 // Do not force garbage collection
      alive = views.Count(v => v.IsAlive);
      if (alive == 0)
      {
        return 0;
      }
    }

    return alive;
  }
}
