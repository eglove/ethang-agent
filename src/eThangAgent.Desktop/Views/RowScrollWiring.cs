using Avalonia.Controls;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>Sticky auto-scroll for ONE subagent row's transcript (the Progress view's
///     accordion), mirroring the main chat's pattern: ScrollChanged feeds the row's
///     controller, the row's Entries CollectionChanged queues a coalesced ScrollToEnd for THAT
///     row's viewer, and geometry drift while pinned re-pins after layout.
///     <para>It lives beside the views rather than inside one of them because the accordion
///     renders in the workspace tab's rail (issue #307 D6) while the same wiring shape serves
///     the cell's own transcript.</para></summary>
internal sealed class RowScrollWiring : IDisposable
{
  private readonly SubagentRowViewModel _row;
  private readonly ScrollViewer _viewer;
  private bool _scrollToEndQueued;

  public RowScrollWiring(SubagentRowViewModel row, ScrollViewer viewer)
  {
    _row = row;
    _viewer = viewer;
    _row.Transcript.Entries.CollectionChanged += OnEntriesChanged;
    viewer.ScrollChanged += OnScrollChanged;
  }

  // Same entry-shape filter as the main chat: appends and streamed tail replaces follow
  // while stuck; other actions never scroll.
  private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
  {
    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
    {
      if (e.NewItems is { Count: > 0 } added && added[0] is UserMessageEntry)
      {
        return;
      }
    }
    else if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
    {
      return;
    }

    if (!_row.Transcript.Scroll.ShouldAutoScroll(isUserEntry: false))
    {
      return;
    }

    _scrollToEndQueued = true;
    Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
  }

  // One queued auto-scroll after layout settles; coalesces the burst of entries that can
  // arrive inside one dispatcher turn.
  private void FlushQueuedScrollToEnd()
  {
    if (!_scrollToEndQueued)
    {
      return;
    }

    _scrollToEndQueued = false;
    _viewer.ScrollToEnd();
  }

  private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
  {
    TranscriptScrollController controller = _row.Transcript.Scroll;
    controller.ObserveScroll(_viewer.Extent.Height, _viewer.Viewport.Height, _viewer.Offset.Y);

    // Geometry drift while pinned (the accordion expanding a sibling row re-lays-out this
    // one) moved the tail out of view: re-pin, coalesced through the same one-shot queue.
    if (controller.ShouldRePin)
    {
      controller.ClearRePin();
      _scrollToEndQueued = true;
      Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
    }
  }

  public void Dispose()
  {
    _row.Transcript.Entries.CollectionChanged -= OnEntriesChanged;
    _viewer.ScrollChanged -= OnScrollChanged;
  }
}
