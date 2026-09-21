using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>One registered screenshot frame: the delivered raster, the screen-space
///     window rect it was sampled from, and whether the model may click into it.
///     Non-actionable rasters are still REGISTERED (their bounding rect is the
///     cleanest miss reason a stale coordinate can carry) but never RESOLVE.</summary>
public sealed record ScreenshotFrame(
    string FrameId,
    int PixelWidth,
    int PixelHeight,
    ScreenshotWindowRect WindowRect,
    bool Actionable,
    DateTimeOffset RegisteredAt);

/// <summary>The screen-space rect a raster was sampled from, in global screen points
///     (x, y, width, height): the outer side of the delivered-raster-to-screen mapping.
///     </summary>
public sealed record ScreenshotWindowRect(int X, int Y, int Width, int Height);

/// <summary>The typed misses of a coordinate resolution: exactly one reason, never a
///     throw. Callers map every miss to STALE_STATE plus a re-observe instruction;
///     the reason only chooses the message.</summary>
#pragma warning disable CA1034 // Named decision: the miss discriminators ARE the contract (the spec's closed miss set); public nested cases keep the case names as declared, namespace-qualified at use sites.
public abstract record FrameResolutionMiss
{
  /// <summary>The frame id is not in the registry (or has expired).</summary>
  public sealed record UnknownFrame : FrameResolutionMiss;

  /// <summary>The frame was registered as non-actionable (e.g. a blank raster).</summary>
  public sealed record UnactionableFrame : FrameResolutionMiss;

  /// <summary>The pixel lies outside the delivered raster.</summary>
  public sealed record PixelOutOfRange : FrameResolutionMiss;
#pragma warning restore CA1034

  private FrameResolutionMiss()
  {
  }
}

/// <summary>A successful coordinate resolution: the global screen point, plus the
///     frame it resolved in (for the ACL's debug trail and tests).</summary>
public sealed record FrameResolution(string FrameId, double X, double Y);

/// <summary>Coordinate resolution outcome: a hit or one typed miss.</summary>
#pragma warning disable CA1034 // Named decision: the hit/miss split IS the contract; see the FrameResolutionMiss ruling.
public abstract record FrameCoordinateResult
{
  /// <summary>The coordinate resolved to a global screen point.</summary>
  public sealed record Hit(FrameResolution Resolution) : FrameCoordinateResult;

  /// <summary>The coordinate missed for the carried typed reason.</summary>
  public sealed record Miss(FrameResolutionMiss Reason) : FrameCoordinateResult;
#pragma warning restore CA1034

  private FrameCoordinateResult()
  {
  }
}

/// <summary>The LRU + TTL registry of delivered screenshot frames (spec 3.4): the
///     model addresses a coordinate target as (frame id | latest, x, y) in DELIVERED
///     RASTER PIXELS; the registry maps it to global screen points through the
///     pixel-center fraction of the delivered raster onto the source window rect.
///     Capacity is bounded (LRU, 16); entries expire after the TTL (injectable
///     clock); not thread-safe for reads vs writes - the gate covers the dict.
///     </summary>
public sealed class FrameRegistry
{
#pragma warning disable IDE0290 // Named decision: the optional clock parameter is part of the public API shape (injectable for TTL tests); a primary constructor would make the default-value seam invisible.
  private const int Capacity = 16;
  internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

  private readonly Lock _gate = new();
  private readonly Dictionary<string, LinkedListNode<Entry>> _byId = [];
  private readonly LinkedList<Entry> _lru = [];
  private readonly Func<DateTimeOffset> _clock;

  /// <summary>Creates a registry; the clock defaults to UTC now and is injectable
  ///     for TTL tests.</summary>
#pragma warning disable IDE0290 // Named decision: the optional clock parameter is the public API's test seam; a primary constructor hides the default-value seam.
  public FrameRegistry(Func<DateTimeOffset>? clock = null) => _clock = clock ?? new Func<DateTimeOffset>(static () => DateTimeOffset.UtcNow);
#pragma warning restore IDE0290

  /// <summary>Registers a frame's raster and source rect, returning the frame
  ///     reference the model later targets coordinates with. Registration never
  ///     fails for capacity reasons - the LRU eviction is silent.</summary>
  public ComputerFrameRef Add(int pixelWidth, int pixelHeight, ScreenshotWindowRect windowRect, bool actionable)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
    ArgumentNullException.ThrowIfNull(windowRect);

    lock (_gate)
    {
      string id = NewFrameId();
      ScreenshotFrame frame = new(id, pixelWidth, pixelHeight, windowRect, actionable, _clock());
      _byId[id] = _lru.AddFirst(new Entry(frame));
      EvictOverCapacity();
      return new ComputerFrameRef(id, pixelWidth, pixelHeight);
    }
  }

  /// <summary>The newest frame (the coordinate target's 'latest' spelling);
  ///     null when the registry is empty (or every entry expired).</summary>
  public ComputerFrameRef? Latest()
  {
    lock (_gate)
    {
      Expired();
      LinkedListNode<Entry>? node = _lru.First;
      return node is null
        ? null
        : new ComputerFrameRef(node.Value.Frame.FrameId, node.Value.Frame.PixelWidth, node.Value.Frame.PixelHeight);
    }
  }

  /// <summary>Resolves a model coordinate target to a global screen point. Every
  ///     resolution ATTEMPT - failed resolves included - refreshes the touched
  ///     frame's recency: recency means last use attempt, by design. The id
  ///     spelling 'latest' (case-insensitive) resolves against the newest frame;
  ///     anything else is a frame id. The mapping is: model pixel -> pixel-center
  ///     fraction of the delivered raster -> source window rect -> screen points
  ///     (floored to whole points at the last step - click targets are integral).
  ///     </summary>
  public FrameCoordinateResult ResolveForCoordinate(string frameId, int x, int y)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(frameId);

    lock (_gate)
    {
      Expired();
      string key = frameId;
      if (string.Equals(frameId, LatestToken, StringComparison.OrdinalIgnoreCase))
      {
        LinkedListNode<Entry>? head = _lru.First;
        if (head is null)
        {
          return new FrameCoordinateResult.Miss(new FrameResolutionMiss.UnknownFrame());
        }

        key = head.Value.Frame.FrameId;
      }

      if (!_byId.TryGetValue(key, out LinkedListNode<Entry>? found))
      {
        return new FrameCoordinateResult.Miss(new FrameResolutionMiss.UnknownFrame());
      }

      _lru.Remove(found);
      _lru.AddFirst(found);
      ScreenshotFrame frame = found.Value.Frame;
      if (!frame.Actionable)
      {
        return new FrameCoordinateResult.Miss(new FrameResolutionMiss.UnactionableFrame());
      }

      if ((x < 0) || (y < 0) || (x >= frame.PixelWidth) || (y >= frame.PixelHeight))
      {
        return new FrameCoordinateResult.Miss(new FrameResolutionMiss.PixelOutOfRange());
      }

      ScreenshotWindowRect rect = frame.WindowRect;
      double fx = (x + 0.5) / frame.PixelWidth;
      double fy = (y + 0.5) / frame.PixelHeight;
      double sx = rect.X + (fx * rect.Width);
      double sy = rect.Y + (fy * rect.Height);
      return new FrameCoordinateResult.Hit(new FrameResolution(frame.FrameId, Math.Floor(sx), Math.Floor(sy)));
    }
  }

  /// <summary>The current entry count (visible for the debug trail and tests).</summary>
  public int Count
  {
    get
    {
      lock (_gate)
      {
        Expired();
        return _byId.Count;
      }
    }
  }

  private void EvictOverCapacity()
  {
    while (_byId.Count > Capacity)
    {
      LinkedListNode<Entry>? last = _lru.Last;
      if (last is null)
      {
        return;
      }

      _ = _byId.Remove(last.Value.Frame.FrameId);
      _lru.Remove(last);
    }
  }

  private void Expired()
  {
    DateTimeOffset now = _clock();
    LinkedListNode<Entry>? node = _lru.First;
    while (node is not null)
    {
      LinkedListNode<Entry>? next = node.Next;
      if (now - node.Value.Frame.RegisteredAt >= Ttl)
      {
        _ = _byId.Remove(node.Value.Frame.FrameId);
        _lru.Remove(node);
      }

      node = next;
    }
  }

  private static string NewFrameId() => "frame-" + Guid.NewGuid().ToString("N")[..8];

  private sealed record Entry(ScreenshotFrame Frame);

  /// <summary>The coordinate target's 'newest frame' spelling.</summary>
  public const string LatestToken = "latest";
}
