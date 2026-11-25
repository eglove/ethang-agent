namespace eThangAgent.ToolDomain;

/// <summary>The deck's outline: the file's content-true version at the read moment, the
///     slide count, every slide summary in presentation order, the deck's embedded media,
///     and the deck's distinct layout display names (the list a LayoutNotFound refusal
///     quotes back).</summary>
public sealed record PptxOutline
{
  /// <summary>Programmer-error guard: a negative count, a null list, or a slide list whose
  ///     length disagrees with the count.</summary>
  /// <param name="version">The file's version at the read moment.</param>
  /// <param name="slideCount">The deck's slide count.</param>
  /// <param name="slides">Every slide summary, in presentation order.</param>
  /// <param name="media">The deck's embedded media parts.</param>
  /// <param name="layoutNames">The deck's distinct layout display names.</param>
  public PptxOutline(FileVersion version, int slideCount, IReadOnlyList<PptxSlideSummary> slides,
      IReadOnlyList<PptxMediaInfo> media, IReadOnlyList<string> layoutNames)
  {
    ArgumentNullException.ThrowIfNull(slides);
    ArgumentNullException.ThrowIfNull(media);
    ArgumentNullException.ThrowIfNull(layoutNames);
    ArgumentOutOfRangeException.ThrowIfNegative(slideCount);
    if (slides.Count != slideCount)
    {
      throw new ArgumentException($"Slides must carry exactly slideCount ({slideCount}) entries; got {slides.Count}.", nameof(slides));
    }

    Version = version;
    SlideCount = slideCount;
    Slides = [.. slides];
    Media = [.. media];
    LayoutNames = [.. layoutNames];
  }

  /// <summary>The file's content-true version at the read moment.</summary>
  public FileVersion Version { get; }

  /// <summary>The deck's slide count.</summary>
  public int SlideCount { get; }

  /// <summary>Every slide summary, in presentation order.</summary>
  public IReadOnlyList<PptxSlideSummary> Slides { get; }

  /// <summary>The deck's embedded media parts.</summary>
  public IReadOnlyList<PptxMediaInfo> Media { get; }

  /// <summary>The deck's distinct layout display names.</summary>
  public IReadOnlyList<string> LayoutNames { get; }
}

/// <summary>One slide's outline line: its 1-based number, its title text (empty when the
///     slide has no title placeholder), the first line of its body text (empty when it has
///     none), whether the slide is hidden, and its layout's display name.</summary>
public sealed record PptxSlideSummary
{
  /// <summary>Programmer-error guard: slide numbers are 1-based and no member is null.
  ///     </summary>
  /// <param name="number">The 1-based slide number.</param>
  /// <param name="title">The slide's title text, or the empty string.</param>
  /// <param name="firstLine">The first line of the slide's body text, or the empty string.</param>
  /// <param name="hidden">Whether the slide is hidden.</param>
  /// <param name="layoutName">The slide's layout display name.</param>
  public PptxSlideSummary(int number, string title, string firstLine, bool hidden, string layoutName)
  {
    ArgumentNullException.ThrowIfNull(title);
    ArgumentNullException.ThrowIfNull(firstLine);
    ArgumentNullException.ThrowIfNull(layoutName);
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
    Number = number;
    Title = title;
    FirstLine = firstLine;
    Hidden = hidden;
    LayoutName = layoutName;
  }

  /// <summary>The 1-based slide number.</summary>
  public int Number { get; }

  /// <summary>The slide's title text, or the empty string.</summary>
  public string Title { get; }

  /// <summary>The first line of the slide's body text, or the empty string.</summary>
  public string FirstLine { get; }

  /// <summary>Whether the slide is hidden.</summary>
  public bool Hidden { get; }

  /// <summary>The slide's layout display name.</summary>
  public string LayoutName { get; }
}

/// <summary>One slide in full: the file's content-true version at the read moment, the
///     1-based slide number, the layout's display name, the hidden flag, every shape in
///     the slide's shape tree in document order, the speaker notes as plain text (empty
///     when the slide has none, never null), the deck's media, the slide's comments, and
///     its animation effects in playback order.</summary>
public sealed record PptxSlideSnapshot
{
  /// <summary>Programmer-error guard: slide numbers are 1-based and no member is null.
  ///     </summary>
  /// <param name="version">The file's version at the read moment.</param>
  /// <param name="slide">The slide's own content: number, layout, hidden flag, shapes.</param>
  /// <param name="notes">The speaker notes as plain text, or the empty string.</param>
  /// <param name="media">The deck's embedded media parts.</param>
  /// <param name="comments">The slide's comments.</param>
  /// <param name="animations">The slide's animation effects, in playback order.</param>
  public PptxSlideSnapshot(FileVersion version, PptxSlideContent slide, string notes,
      IReadOnlyList<PptxMediaInfo> media, IReadOnlyList<PptxCommentInfo> comments,
      IReadOnlyList<PptxAnimationInfo> animations)
  {
    ArgumentNullException.ThrowIfNull(slide);
    ArgumentNullException.ThrowIfNull(notes);
    ArgumentNullException.ThrowIfNull(media);
    ArgumentNullException.ThrowIfNull(comments);
    ArgumentNullException.ThrowIfNull(animations);
    Version = version;
    Number = slide.Number;
    LayoutName = slide.LayoutName;
    Hidden = slide.Hidden;
    Shapes = slide.Shapes;
    Notes = notes;
    Media = [.. media];
    Comments = [.. comments];
    Animations = [.. animations];
  }

  /// <summary>The file's content-true version at the read moment.</summary>
  public FileVersion Version { get; }

  /// <summary>The 1-based slide number.</summary>
  public int Number { get; }

  /// <summary>The slide's layout display name.</summary>
  public string LayoutName { get; }

  /// <summary>Whether the slide is hidden.</summary>
  public bool Hidden { get; }

  /// <summary>Every shape in the slide's shape tree, in document order.</summary>
  public IReadOnlyList<PptxShapeInfo> Shapes { get; }

  /// <summary>The speaker notes as plain text, or the empty string.</summary>
  public string Notes { get; }

  /// <summary>The deck's embedded media parts.</summary>
  public IReadOnlyList<PptxMediaInfo> Media { get; }

  /// <summary>The slide's comments.</summary>
  public IReadOnlyList<PptxCommentInfo> Comments { get; }

  /// <summary>The slide's animation effects, in playback order.</summary>
  public IReadOnlyList<PptxAnimationInfo> Animations { get; }
}

/// <summary>What a slide IS, independent of the file it was read from: its 1-based
///     number, its layout display name, whether it is hidden, and its shapes in document
///     order. <see cref="PptxSlideSnapshot"/> pairs this with the file's version and the
///     deck-level facts a caller needs, so the slide's own shape stays a single unit.
///     </summary>
public sealed record PptxSlideContent
{
  /// <summary>Programmer-error guard: slide numbers are 1-based and no member is null.
  ///     </summary>
  /// <param name="number">The 1-based slide number.</param>
  /// <param name="layoutName">The slide's layout display name.</param>
  /// <param name="hidden">Whether the slide is hidden.</param>
  /// <param name="shapes">Every shape in the slide's shape tree, in document order.</param>
  public PptxSlideContent(int number, string layoutName, bool hidden, IReadOnlyList<PptxShapeInfo> shapes)
  {
    ArgumentNullException.ThrowIfNull(layoutName);
    ArgumentNullException.ThrowIfNull(shapes);
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
    Number = number;
    LayoutName = layoutName;
    Hidden = hidden;
    Shapes = [.. shapes];
  }

  /// <summary>The 1-based slide number.</summary>
  public int Number { get; }

  /// <summary>The slide's layout display name.</summary>
  public string LayoutName { get; }

  /// <summary>Whether the slide is hidden.</summary>
  public bool Hidden { get; }

  /// <summary>Every shape in the slide's shape tree, in document order.</summary>
  public IReadOnlyList<PptxShapeInfo> Shapes { get; }
}

/// <summary>One embedded media part: its package name, its media type, and its byte
///     count.</summary>
public sealed record PptxMediaInfo
{
  /// <summary>Programmer-error guard: a media part has a non-empty name and a
  ///     non-negative size.</summary>
  /// <param name="name">The media part's package name.</param>
  /// <param name="mediaType">The media part's content type.</param>
  /// <param name="byteCount">The media part's size in bytes.</param>
  public PptxMediaInfo(string name, string mediaType, long byteCount)
  {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
    Name = name;
    MediaType = mediaType;
    ByteCount = byteCount;
  }

  /// <summary>The media part's package name.</summary>
  public string Name { get; }

  /// <summary>The media part's content type.</summary>
  public string MediaType { get; }

  /// <summary>The media part's size in bytes.</summary>
  public long ByteCount { get; }
}

/// <summary>One comment: its id, author, text, the shape address it anchors to, and
///     whether it came from the legacy comment shape (true) rather than the current one.
///     </summary>
public sealed record PptxCommentInfo
{
  /// <summary>Programmer-error guard: a non-negative id, a non-null author and text, and a
  ///     non-empty anchor address.</summary>
  /// <param name="id">The comment id.</param>
  /// <param name="author">The comment's author.</param>
  /// <param name="text">The comment's text.</param>
  /// <param name="anchorAddress">The shape address the comment anchors to.</param>
  /// <param name="legacy">Whether the comment came from the legacy comment shape.</param>
  public PptxCommentInfo(int id, string author, string text, string anchorAddress, bool legacy)
  {
    ArgumentNullException.ThrowIfNull(author);
    ArgumentNullException.ThrowIfNull(text);
    ArgumentException.ThrowIfNullOrEmpty(anchorAddress);
    ArgumentOutOfRangeException.ThrowIfNegative(id);
    Id = id;
    Author = author;
    Text = text;
    AnchorAddress = anchorAddress;
    Legacy = legacy;
  }

  /// <summary>The comment id.</summary>
  public int Id { get; }

  /// <summary>The comment's author.</summary>
  public string Author { get; }

  /// <summary>The comment's text.</summary>
  public string Text { get; }

  /// <summary>The shape address the comment anchors to.</summary>
  public string AnchorAddress { get; }

  /// <summary>Whether the comment came from the legacy comment shape.</summary>
  public bool Legacy { get; }
}

/// <summary>One animation effect in playback order: its 1-based index, the address of
///     the shape it targets, its effect name, its trigger, and its duration as text.
///     </summary>
public sealed record PptxAnimationInfo
{
  /// <summary>Programmer-error guard: effect indices are 1-based, the address is
  ///     non-empty, and effect, trigger and duration are non-null.</summary>
  /// <param name="index">The 1-based playback index.</param>
  /// <param name="address">The address of the shape the effect targets.</param>
  /// <param name="effect">The effect name.</param>
  /// <param name="trigger">The effect's trigger.</param>
  /// <param name="duration">The effect's duration as text.</param>
  public PptxAnimationInfo(int index, string address, string effect, string trigger, string duration)
  {
    ArgumentException.ThrowIfNullOrEmpty(address);
    ArgumentNullException.ThrowIfNull(effect);
    ArgumentNullException.ThrowIfNull(trigger);
    ArgumentNullException.ThrowIfNull(duration);
    ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
    Index = index;
    Address = address;
    Effect = effect;
    Trigger = trigger;
    Duration = duration;
  }

  /// <summary>The 1-based playback index.</summary>
  public int Index { get; }

  /// <summary>The address of the shape the effect targets.</summary>
  public string Address { get; }

  /// <summary>The effect name.</summary>
  public string Effect { get; }

  /// <summary>The effect's trigger.</summary>
  public string Trigger { get; }

  /// <summary>The effect's duration as text.</summary>
  public string Duration { get; }
}

/// <summary>One table cell's text: the row/column coordinates (1-based, matching the cell
///     address grammar) and the cell's plain text.</summary>
public sealed record PptxCellText
{
  /// <summary>Programmer-error guard: cell coordinates are 1-based and the text is
  ///     non-null.</summary>
  /// <param name="row">The 1-based row.</param>
  /// <param name="col">The 1-based column.</param>
  /// <param name="text">The cell's plain text.</param>
  public PptxCellText(int row, int col, string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(col, 1);
    Row = row;
    Col = col;
    Text = text;
  }

  /// <summary>The 1-based row.</summary>
  public int Row { get; }

  /// <summary>The 1-based column.</summary>
  public int Col { get; }

  /// <summary>The cell's plain text.</summary>
  public string Text { get; }
}

/// <summary>The result of an atomic edit batch: the file's new content-true version and
///     how many shapes changed.</summary>
public sealed record PptxEditOutcome
{
  /// <summary>Programmer-error guard: a changed count is never negative.</summary>
  /// <param name="newVersion">The file's version after the batch.</param>
  /// <param name="changedCount">How many shapes the batch changed.</param>
  public PptxEditOutcome(FileVersion newVersion, int changedCount)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(changedCount);
    NewVersion = newVersion;
    ChangedCount = changedCount;
  }

  /// <summary>The file's content-true version after the batch.</summary>
  public FileVersion NewVersion { get; }

  /// <summary>How many shapes the batch changed.</summary>
  public int ChangedCount { get; }
}

/// <summary>The result of a create or comment mutation: the file's new content-true
///     version.</summary>
public sealed record PptxSaveOutcome(FileVersion NewVersion);


/// <summary>One shape in a slide's shape tree: its 1-based index among its siblings, the
///     address that reaches it, and its kind ('text', 'picture', 'table', 'chart',
///     'group'). The five subtypes below are the whole vocabulary; a chart is a shape,
///     never a separate address form.</summary>
public abstract record PptxShapeInfo
{
  /// <summary>Programmer-error guard: shape indices are 1-based, the address is non-empty
  ///     and the kind is non-empty. The ACL constructs these from a read; model input
  ///     never does.</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="kind">The shape's kind.</param>
  protected PptxShapeInfo(int index, string address, string kind)
  {
    ArgumentException.ThrowIfNullOrEmpty(address);
    ArgumentException.ThrowIfNullOrEmpty(kind);
    ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
    Index = index;
    Address = address;
    Kind = kind;
  }

  /// <summary>The 1-based index among the shape's siblings.</summary>
  public int Index { get; }

  /// <summary>The address that reaches the shape.</summary>
  public string Address { get; }

  /// <summary>The shape's kind: 'text', 'picture', 'table', 'chart' or 'group'.</summary>
  public string Kind { get; }
}

/// <summary>A text box: its full text with paragraphs joined by a newline and the runs
///     within a paragraph concatenated, whether it is a title placeholder, and whether it
///     is any placeholder at all.</summary>
public sealed record PptxTextBoxShape : PptxShapeInfo
{
  /// <summary>Programmer-error guard: the text is never null (an empty box is the empty
  ///     string).</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="text">The box's full text.</param>
  /// <param name="isTitle">Whether the box is a title placeholder.</param>
  /// <param name="isPlaceholder">Whether the box is any placeholder.</param>
  public PptxTextBoxShape(int index, string address, string text, bool isTitle, bool isPlaceholder)
      : base(index, address, "text")
  {
    ArgumentNullException.ThrowIfNull(text);
    Text = text;
    IsTitle = isTitle;
    IsPlaceholder = isPlaceholder;
  }

  /// <summary>The box's full text.</summary>
  public string Text { get; }

  /// <summary>Whether the box is a title placeholder.</summary>
  public bool IsTitle { get; }

  /// <summary>Whether the box is any placeholder.</summary>
  public bool IsPlaceholder { get; }
}

/// <summary>A picture: the package name of the media part it displays.</summary>
public sealed record PptxPictureShape : PptxShapeInfo
{
  /// <summary>Programmer-error guard: a picture always names its media part.</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="mediaName">The package name of the picture's media part.</param>
  public PptxPictureShape(int index, string address, string mediaName)
      : base(index, address, "picture")
  {
    ArgumentException.ThrowIfNullOrEmpty(mediaName);
    MediaName = mediaName;
  }

  /// <summary>The package name of the picture's media part.</summary>
  public string MediaName { get; }
}

/// <summary>A table: its dimensions and every cell's text. A table is one shape; its cells
///     address through s&lt;N&gt;.sh&lt;M&gt;.t&lt;R&gt;.c&lt;C&gt;.</summary>
public sealed record PptxTableShape : PptxShapeInfo
{
  /// <summary>Programmer-error guard: dimensions are at least 1 and the cell list carries
  ///     exactly rows times cols entries.</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="rows">The table's row count.</param>
  /// <param name="cols">The table's column count.</param>
  /// <param name="cells">Every cell's text.</param>
  public PptxTableShape(int index, string address, int rows, int cols, IReadOnlyList<PptxCellText> cells)
      : base(index, address, "table")
  {
    ArgumentNullException.ThrowIfNull(cells);
    ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
    if (cells.Count != rows * cols)
    {
      throw new ArgumentException($"Cells must carry exactly rows x cols ({rows * cols}) entries; got {cells.Count}.", nameof(cells));
    }

    Rows = rows;
    Cols = cols;
    Cells = [.. cells];
  }

  /// <summary>The table's row count.</summary>
  public int Rows { get; }

  /// <summary>The table's column count.</summary>
  public int Cols { get; }

  /// <summary>Every cell's text.</summary>
  public IReadOnlyList<PptxCellText> Cells { get; }
}

/// <summary>A chart: its type, its series names and its category names.</summary>
public sealed record PptxChartShape : PptxShapeInfo
{
  /// <summary>Programmer-error guard: the chart type is non-empty and neither name list
  ///     is null.</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="chartType">The chart's type.</param>
  /// <param name="seriesNames">The chart's series names.</param>
  /// <param name="categoryNames">The chart's category names.</param>
  public PptxChartShape(int index, string address, string chartType, IReadOnlyList<string> seriesNames,
      IReadOnlyList<string> categoryNames)
      : base(index, address, "chart")
  {
    ArgumentException.ThrowIfNullOrEmpty(chartType);
    ArgumentNullException.ThrowIfNull(seriesNames);
    ArgumentNullException.ThrowIfNull(categoryNames);
    ChartType = chartType;
    SeriesNames = [.. seriesNames];
    CategoryNames = [.. categoryNames];
  }

  /// <summary>The chart's type.</summary>
  public string ChartType { get; }

  /// <summary>The chart's series names.</summary>
  public IReadOnlyList<string> SeriesNames { get; }

  /// <summary>The chart's category names.</summary>
  public IReadOnlyList<string> CategoryNames { get; }
}

/// <summary>A group shape: one shape at the top level whose children address through it
///     (s&lt;N&gt;.sh&lt;M&gt;.sh&lt;K&gt;).</summary>
public sealed record PptxGroupShape : PptxShapeInfo
{
  /// <summary>Programmer-error guard: the child list is never null.</summary>
  /// <param name="index">The 1-based index among the shape's siblings.</param>
  /// <param name="address">The address that reaches the shape.</param>
  /// <param name="children">The group's children, in document order.</param>
  public PptxGroupShape(int index, string address, IReadOnlyList<PptxShapeInfo> children)
      : base(index, address, "group")
  {
    ArgumentNullException.ThrowIfNull(children);
    Children = [.. children];
  }

  /// <summary>The group's children, in document order.</summary>
  public IReadOnlyList<PptxShapeInfo> Children { get; }
}

/// <summary>One edit operation inside an atomic batch. All operations are validated and
///     resolved BEFORE any mutation; the first invalid op refuses the whole batch. The
///     marker record is deliberately empty: it exists to type the closed operation
///     vocabulary, not to carry members.</summary>
#pragma warning disable S2094 // Named decision: deliberate empty marker record typing the closed op vocabulary.
public abstract record PptxOperation;
#pragma warning restore S2094

/// <summary>Replaces the addressed text box's text. The box keeps its position, size and
///     formatting; the first run's character formatting applies to the new text.</summary>
public sealed record ReplaceText(string Address, string Text) : PptxOperation;

/// <summary>Replaces a slide's speaker notes; a slide with no notes part gains one.
///     </summary>
public sealed record SetNotes(int Slide, string Text) : PptxOperation;

/// <summary>Inserts a slide built on the named layout immediately after AfterSlide
///     (0 inserts at the front). An unknown layout name fails LayoutNotFound.</summary>
public sealed record InsertSlide(int AfterSlide, string LayoutName) : PptxOperation;

/// <summary>Deletes one slide and renumbers the following slides. Deleting the last
///     remaining slide fails StrandedContent.</summary>
public sealed record DeleteSlide(int Slide) : PptxOperation;

/// <summary>Moves a slide to a new position and renumbers. A no-op move is accepted.
///     </summary>
public sealed record MoveSlide(int From, int To) : PptxOperation;

/// <summary>Moves the slide's content onto the named layout's placeholders, leaving
///     content that does not map in place; a change that would strand a shape fails
///     StrandedContent naming it.</summary>
public sealed record SetSlideLayout(int Slide, string LayoutName) : PptxOperation;

/// <summary>Swaps the image bytes behind the addressed picture, keeping its position,
///     size, crop and effects. The new file must be PNG or JPEG.</summary>
public sealed record ReplacePicture(string Address, string ImagePath) : PptxOperation;

/// <summary>Adds a picture to the slide at the given offset and size in English Metric
///     Units. The file must be PNG or JPEG.</summary>
public sealed record InsertPicture(int Slide, string ImagePath, long X, long Y, long Width, long Height) : PptxOperation;

/// <summary>Adds a table with the given cells, dimensions and position. The cell grid must
///     match Rows x Cols; a mismatch fails InvalidAddress.</summary>
public sealed record InsertTable(int Slide, int Rows, int Cols, IReadOnlyList<IReadOnlyList<string>> Cells, long X, long Y, long Width, long Height) : PptxOperation;

/// <summary>Adds a text box with the given text and geometry in English Metric Units.
///     </summary>
public sealed record InsertTextBox(int Slide, string Text, long X, long Y, long Width, long Height) : PptxOperation;

/// <summary>Changes a chart's values, series names and category names. Null members are
///     left unchanged. The chart's type is never expressible here.</summary>
public sealed record SetChartData(string Address, IReadOnlyList<string>? SeriesNames, IReadOnlyList<string>? CategoryNames, IReadOnlyList<IReadOnlyList<double>>? Values) : PptxOperation;

/// <summary>Retimes, retriggers or replaces one existing animation effect; null members
///     are left unchanged. An index one past the slide's last effect adds a simple
///     entrance effect on the addressed shape.</summary>
public sealed record SetAnimation(int Slide, int EffectIndex, string? Effect, string? Trigger, int? DurationMilliseconds) : PptxOperation;

/// <summary>Removes one animation effect and renumbers the following ones.</summary>
public sealed record RemoveAnimation(int Slide, int EffectIndex) : PptxOperation;
