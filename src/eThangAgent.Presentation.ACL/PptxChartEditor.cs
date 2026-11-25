using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it.
#pragma warning disable S3220, S3878
namespace eThangAgent.Presentation.ACL;

/// <summary>A chart address resolved to the chart part it names.</summary>
/// <param name="Address">The parsed address.</param>
/// <param name="ChartPart">The chart part the addressed shape references.</param>
internal sealed record PptxChartTarget(PptxAddress Address, ChartPart ChartPart);

/// <summary>The state a chart edit writes: the series names, the category names, the
///     values, and whether the cached values are rewritten. A rename leaves the value
///     caches alone but still rewrites the workbook from them, so the chart and its data
///     never disagree.</summary>
/// <param name="SeriesNames">The series names the chart carries after the edit.</param>
/// <param name="CategoryNames">The category names the chart carries after the edit.</param>
/// <param name="Values">The values the chart carries after the edit, row per series.</param>
/// <param name="WritesValues">Whether the cached values are rewritten.</param>
internal sealed record PptxChartPlan(IReadOnlyList<string> SeriesNames, IReadOnlyList<string> CategoryNames,
    IReadOnlyList<IReadOnlyList<double>> Values, bool WritesValues);

/// <summary>The chart data edit: values, series names and category names on a chart the
///     deck already has. A chart's numbers live in two places - the chart part's cached
///     values and its embedded workbook - so both are written in one edit; writing one
///     alone leaves a chart that shows stale numbers after a refresh in PowerPoint. A
///     chart whose type is outside bar, column, line and pie is refused rather than
///     approximated, and a chart with no embedded workbook is refused rather than having
///     values invented for it.</summary>
internal static class PptxChartEditor
{
  /// <summary>The element name every chart family spells its series with.</summary>
  private const string SeriesElementName = "ser";

  /// <summary>Resolves a chart data edit, or null when the operation belongs to another
  ///     task.</summary>
  /// <param name="slides">The deck's slides in presentation order.</param>
  /// <param name="operation">The operation to resolve.</param>
  internal static Result<PptxResolvedEdit>? Resolve(IReadOnlyList<SlidePart> slides, PptxOperation operation)
    => operation is SetChartData set ? ResolveSetChartData(slides, set) : null;

  /// <summary>Resolves a SetChartData operation: the address must name a chart, the
  ///     chart's family must be supported, its embedded workbook must exist, and the
  ///     supplied arrays must match the chart's dimensions. Nothing mutates until every
  ///     one of those holds.</summary>
  private static Result<PptxResolvedEdit> ResolveSetChartData(IReadOnlyList<SlidePart> slides, SetChartData operation)
  {
    Result<PptxChartTarget> target = Target(slides, operation.Address);
    if (!target.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(target.Error);
    }

    PptxChartTarget chart = target.Value;
    Result<C.PlotArea> plotArea = PlotAreaOf(chart.ChartPart);
    if (!plotArea.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(plotArea.Error);
    }

    if (Family(plotArea.Value) is not { } family)
    {
      string type = PptxShapeWalker.ChartType(plotArea.Value);
      return Failed(PptxErrorCodes.ChartTypeUnsupported,
          $"The chart at '{chart.Address.Render()}' is a {type} chart; chart data can be edited on bar, column, line and pie charts only.");
    }

    if (chart.ChartPart.EmbeddedPackagePart is not { } workbook)
    {
      return Failed(PptxErrorCodes.ChartDataMissing,
          $"The chart at '{chart.Address.Render()}' carries no embedded workbook, so its values cannot be written; nothing was changed.");
    }

    Result<PptxChartPlan> plan = Plan(chart, family, operation);
    if (!plan.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(plan.Error);
    }

    PptxChartPlan resolved = plan.Value;
    return Result.Success(new PptxResolvedEdit(chart.Address.Render(),
        () => Apply(chart.ChartPart, workbook, family, resolved)));
  }

  /// <summary>The address resolved to the chart part it names. An address that does not
  ///     parse is InvalidAddress, one naming a slide the deck does not have is
  ///     SlideNotFound, and one naming a shape that is not a chart is AddressNotFound.
  ///     </summary>
  private static Result<PptxChartTarget> Target(IReadOnlyList<SlidePart> slides, string address)
  {
    Result<PptxAddress> parsed = PptxAddress.Parse(address);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<PptxChartTarget>(parsed.Error);
    }

    PptxAddress parsedAddress = parsed.Value;
    if (parsedAddress.Kind != PptxAddressKind.Shape)
    {
      return Failure(PptxErrorCodes.InvalidAddress,
          $"Address '{address}' does not name a shape; a chart is addressed as s<N>.sh<M>.");
    }

    if (parsedAddress.Slide < 1 || parsedAddress.Slide > slides.Count)
    {
      return Failure(PptxErrorCodes.SlideNotFound,
          $"Slide {parsedAddress.Slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}.");
    }

    SlidePart slide = slides[parsedAddress.Slide - 1];
    OpenXmlElement? element = PptxShapeWalker.ElementAt(slide, parsedAddress.ShapePath);
    return element is null
        ? Failure(PptxErrorCodes.AddressNotFound,
            $"Address '{address}' resolves to no shape on slide {parsedAddress.Slide}.")
        : Found(slide, element, parsedAddress, address);
  }

  /// <summary>The chart target an element that exists resolves to, or the refusal naming
  ///     the address that reaches no chart.</summary>
  private static Result<PptxChartTarget> Found(SlidePart slide, OpenXmlElement element, PptxAddress address,
      string text)
    => ChartPartOf(slide, element) is { } chart
        ? Result.Success(new PptxChartTarget(address, chart))
        : Failure(PptxErrorCodes.AddressNotFound,
            $"Address '{text}' resolves to no chart on slide {address.Slide}.");

  /// <summary>The chart part a shape references, or null when the shape is not a chart
  ///     frame or its relationship reaches no chart part.</summary>
  private static ChartPart? ChartPartOf(SlidePart slide, OpenXmlElement element)
  {
    if (element is not P.GraphicFrame frame || frame.Graphic?.GraphicData?.GetFirstChild<C.ChartReference>() is not { } reference)
    {
      return null;
    }

    string? relationshipId = reference.Id?.Value;
    return relationshipId is not null && slide.Parts.FirstOrDefault(pair => pair.RelationshipId == relationshipId).OpenXmlPart is ChartPart chart
        ? chart
        : null;
  }

  /// <summary>The chart's plot area, or a refusal when the part carries none.</summary>
  private static Result<C.PlotArea> PlotAreaOf(ChartPart chartPart)
    => chartPart.ChartSpace?.GetFirstChild<C.Chart>()?.PlotArea is { } plotArea
        ? Result.Success(plotArea)
        : Result.Failure<C.PlotArea>(new DomainError(PptxErrorCodes.CorruptPresentation,
            $"The chart part '{chartPart.Uri.OriginalString}' carries no plot area."));

  /// <summary>The family element of a supported chart type, or null when the type is
  ///     outside bar, column, line and pie.</summary>
  private static OpenXmlElement? Family(C.PlotArea plotArea) => PptxShapeWalker.ChartType(plotArea) switch
  {
    "bar" or "column" => plotArea.GetFirstChild<C.BarChart>(),
    "line" => plotArea.GetFirstChild<C.LineChart>(),
    "pie" => plotArea.GetFirstChild<C.PieChart>(),
    _ => null,
  };

  /// <summary>The series elements of a chart family, in the order the chart plots them.
  ///     </summary>
  private static List<OpenXmlElement> Series(OpenXmlElement family)
    => [.. family.ChildElements.Where(child => child.LocalName == SeriesElementName)];

  /// <summary>Builds the state the edit writes, refusing an array whose shape does not
  ///     match the chart's dimensions. The category count the values must carry is the
  ///     count the chart will have afterwards: the supplied category names when both are
  ///     supplied, otherwise the chart's current count.</summary>
  private static Result<PptxChartPlan> Plan(PptxChartTarget chart, OpenXmlElement family, SetChartData operation)
  {
    List<OpenXmlElement> series = Series(family);
    if (series.Count == 0)
    {
      return PlanFailure(PptxErrorCodes.ChartDataMissing,
          $"The chart at '{chart.Address.Render()}' carries no data series, so there is nothing to change.");
    }

    List<string> currentNames = [.. series.Select((element, position) => SeriesName(element) ?? DefaultSeriesName(position))];
    List<string> currentCategories = Categories(series);
    int currentCategoryCount = currentCategories.Count > 0 ? currentCategories.Count : ValueCount(series[0]);
    Result<int> dimensions = Dimensions(operation, series.Count, currentCategoryCount);
    if (!dimensions.IsSuccess)
    {
      return Result.Failure<PptxChartPlan>(dimensions.Error);
    }

    int categoryCount = dimensions.Value;
    IReadOnlyList<string> names = operation.SeriesNames ?? currentNames;
    List<string> categories = operation.CategoryNames is { } supplied
        ? [.. supplied]
        : Fit(currentCategories, categoryCount);
    IReadOnlyList<IReadOnlyList<double>> values = operation.Values ?? ReadValues(series, categoryCount);
    return Result.Success(new PptxChartPlan(names, categories, values, operation.Values is not null));
  }

  /// <summary>The category count the edit must produce, or the refusal naming the
  ///     dimensions the arrays were expected to carry.</summary>
  private static Result<int> Dimensions(SetChartData operation, int seriesCount, int currentCategoryCount)
  {
    IReadOnlyList<IReadOnlyList<double>>? values = operation.Values;
    IReadOnlyList<string>? categories = operation.CategoryNames;
    if (operation.SeriesNames is { } names && names.Count != seriesCount)
    {
      return DimensionFailure(PptxErrorCodes.InvalidAddress,
          $"The chart has {seriesCount} series of {currentCategoryCount} values, so {seriesCount} series names are expected; {names.Count} were supplied.");
    }

    if (values is null)
    {
      return categories is null || categories.Count == currentCategoryCount
          ? Result.Success(currentCategoryCount)
          : DimensionFailure(PptxErrorCodes.InvalidAddress,
              $"The chart has {currentCategoryCount} categories, so {currentCategoryCount} category names are expected; {categories.Count} were supplied. Supply values to change the number of data points.");
    }

    int expected = categories?.Count ?? currentCategoryCount;
    return values.Count != seriesCount
        ? DimensionFailure(PptxErrorCodes.InvalidAddress,
            $"The chart has {seriesCount} series of {expected} values, so {seriesCount} rows are expected; {values.Count} were supplied.")
        : Rows(values, seriesCount, expected);
  }

  /// <summary>The category count the rows agree on, or the refusal naming the widths the
  ///     rows were expected to carry.</summary>
  private static Result<int> Rows(IReadOnlyList<IReadOnlyList<double>> values, int seriesCount, int expected)
    => values.All(row => row.Count == expected)
        ? Result.Success(expected)
        : DimensionFailure(PptxErrorCodes.InvalidAddress,
            $"The chart has {seriesCount} series of {expected} values, so every row must carry {expected} values; the array carries rows of {string.Join(", ", values.Select(row => row.Count))}.");

  /// <summary>The category names fitted to a count: the existing names, extended with
  ///     numbered names for added data points and truncated for removed ones. A label is
  ///     not a value, so naming an added point is not inventing data.</summary>
  private static List<string> Fit(List<string> names, int count)
  {
    List<string> fitted = [.. names.Take(count)];
    while (fitted.Count < count)
    {
      fitted.Add("Category " + (fitted.Count + 1).ToString(CultureInfo.InvariantCulture));
    }

    return fitted;
  }

  /// <summary>The values the chart already carries, fitted to the category count: a
  ///     series whose cache is shorter than the categories reads as zero for the missing
  ///     points, which is what the chart shows.</summary>
  private static List<IReadOnlyList<double>> ReadValues(List<OpenXmlElement> series, int categoryCount)
    => [.. series.Select(element => (IReadOnlyList<double>)FitValues(Values(element), categoryCount))];

  private static List<double> FitValues(List<double> values, int count)
  {
    List<double> fitted = [.. values.Take(count)];
    while (fitted.Count < count)
    {
      fitted.Add(0d);
    }

    return fitted;
  }

  /// <summary>Applies the plan: the cached series names, categories and values in the
  ///     chart part, then the same state in the embedded workbook, so the chart and its
  ///     data agree.</summary>
  private static void Apply(ChartPart chartPart, EmbeddedPackagePart workbook, OpenXmlElement family,
      PptxChartPlan plan)
  {
    List<OpenXmlElement> series = Series(family);
    WriteSeriesNames(series, plan.SeriesNames);
    WriteCategories(series, plan.CategoryNames);
    if (plan.WritesValues)
    {
      WriteValues(series, plan.Values);
    }

    chartPart.ChartSpace?.Save();
    WriteWorkbook(workbook, plan);
  }

  /// <summary>Writes each series' cached name. A series carries its name as a string
  ///     reference or as a literal; whichever it has is rewritten, and a series with
  ///     neither gains a string reference.</summary>
  private static void WriteSeriesNames(List<OpenXmlElement> series, IReadOnlyList<string> names)
  {
    for (int position = 0; position < series.Count && position < names.Count; position++)
    {
      C.SeriesText text = series[position].GetFirstChild<C.SeriesText>() ?? AddSeriesText(series[position]);
      WriteText(text, names[position]);
    }
  }

  /// <summary>The series text element of a series, placed after its order element, which
  ///     is the position the schema declares for it.</summary>
  private static C.SeriesText AddSeriesText(OpenXmlElement series)
  {
    C.SeriesText text = new();
    if (series.GetFirstChild<C.Order>() is { } order)
    {
      _ = order.InsertAfterSelf(text);
    }
    else
    {
      series.Append(text);
    }

    return text;
  }

  private static void WriteText(C.SeriesText text, string value)
  {
    if (text.StringReference is { } reference)
    {
      reference.StringCache = StringCache([value]);
      return;
    }

    if (text.NumericValue is not null)
    {
      text.NumericValue.Text = value;
      return;
    }

    text.StringReference = new C.StringReference(StringCache([value]));
  }

  /// <summary>Writes the category names to every series that carries a category axis
  ///     data element, and to the first series when none does, so the chart's categories
  ///     are stated once and read back from the first series.</summary>
  private static void WriteCategories(List<OpenXmlElement> series, IReadOnlyList<string> categories)
  {
    List<OpenXmlElement> carriers = [.. series.Where(element => element.GetFirstChild<C.CategoryAxisData>() is not null)];
    if (carriers.Count == 0)
    {
      carriers.Add(series[0]);
    }

    foreach (OpenXmlElement element in carriers)
    {
      C.CategoryAxisData data = element.GetFirstChild<C.CategoryAxisData>() ?? AddCategoryAxisData(element);
      WriteCategoryCache(data, categories);
    }
  }

  /// <summary>The category axis data element of a series, placed before its values, which
  ///     is the position the schema declares for it.</summary>
  private static C.CategoryAxisData AddCategoryAxisData(OpenXmlElement series)
  {
    C.CategoryAxisData data = new();
    if (series.GetFirstChild<C.Values>() is { } values)
    {
      _ = values.InsertBeforeSelf(data);
    }
    else
    {
      series.Append(data);
    }

    return data;
  }

  private static void WriteCategoryCache(C.CategoryAxisData data, IReadOnlyList<string> categories)
  {
    if (data.StringReference is { } reference)
    {
      reference.StringCache = StringCache(categories);
      return;
    }

    if (data.StringLiteral is { } literal)
    {
      ReplacePoints(literal, categories);
      return;
    }

    data.StringReference = new C.StringReference(StringCache(categories));
  }

  /// <summary>Writes each series' cached values, one row per series. A series with no
  ///     values element gains a literal one: a formula pointing at a sheet range the edit
  ///     did not write would be a lie.</summary>
  private static void WriteValues(List<OpenXmlElement> series, IReadOnlyList<IReadOnlyList<double>> values)
  {
    for (int position = 0; position < series.Count && position < values.Count; position++)
    {
      C.Values target = series[position].GetFirstChild<C.Values>() ?? AddValues(series[position]);
      WriteValueCache(target, values[position]);
    }
  }

  private static C.Values AddValues(OpenXmlElement series)
  {
    C.Values values = new();
    series.Append(values);
    return values;
  }

  private static void WriteValueCache(C.Values values, IReadOnlyList<double> points)
  {
    if (values.NumberReference is { } reference)
    {
      reference.NumberingCache = NumberCache(points);
      return;
    }

    if (values.NumberLiteral is { } literal)
    {
      ReplaceNumberPoints(literal, points);
      return;
    }

    values.NumberReference = new C.NumberReference(NumberCache(points));
  }

  private static C.StringCache StringCache(IReadOnlyList<string> points)
  {
    C.StringCache cache = new();
    ReplacePoints(cache, points);
    return cache;
  }

  private static C.NumberingCache NumberCache(IReadOnlyList<double> points)
  {
    C.NumberingCache cache = new(new C.FormatCode("General"));
    ReplaceNumberPoints(cache, points);
    return cache;
  }

  /// <summary>Replaces a string cache's points with one point per value, renumbering from
  ///     zero and restating the point count.</summary>
  private static void ReplacePoints(OpenXmlElement cache, IReadOnlyList<string> points)
  {
    cache.RemoveAllChildren<C.StringPoint>();
    if (cache.GetFirstChild<C.PointCount>() is { } count)
    {
      count.Val = (uint)points.Count;
    }
    else
    {
      cache.Append(new C.PointCount { Val = (uint)points.Count });
    }

    for (int position = 0; position < points.Count; position++)
    {
      cache.Append(new C.StringPoint { Index = (uint)position, NumericValue = new C.NumericValue(points[position]) });
    }
  }

  /// <summary>Replaces a numeric cache's points with one point per value, renumbering from
  ///     zero and restating the point count.</summary>
  private static void ReplaceNumberPoints(OpenXmlElement cache, IReadOnlyList<double> points)
  {
    cache.RemoveAllChildren<C.NumericPoint>();
    if (cache.GetFirstChild<C.PointCount>() is { } count)
    {
      count.Val = (uint)points.Count;
    }
    else
    {
      cache.Append(new C.PointCount { Val = (uint)points.Count });
    }

    for (int position = 0; position < points.Count; position++)
    {
      cache.Append(new C.NumericPoint { Index = (uint)position, NumericValue = new C.NumericValue(Number(points[position])) });
    }
  }

  /// <summary>A series' cached name, or null when it carries none.</summary>
  private static string? SeriesName(OpenXmlElement series)
  {
    C.SeriesText? text = series.GetFirstChild<C.SeriesText>();
    return text?.StringReference?.StringCache is { } cache
        ? FirstPoint(cache)
        : text?.NumericValue?.Text;
  }

  /// <summary>The category names a chart carries: the first series' category cache.
  ///     </summary>
  private static List<string> Categories(List<OpenXmlElement> series)
  {
    C.CategoryAxisData? data = series.Select(element => element.GetFirstChild<C.CategoryAxisData>()).FirstOrDefault(found => found is not null);
    OpenXmlElement? cache = data?.StringReference?.StringCache ?? (OpenXmlElement?)data?.StringLiteral;
    return cache is null ? [] : [.. cache.ChildElements.Where(child => child.LocalName == "pt").Select(PointText)];
  }

  /// <summary>The numeric values a series carries.</summary>
  private static List<double> Values(OpenXmlElement series)
  {
    C.Values? values = series.GetFirstChild<C.Values>();
    OpenXmlElement? cache = values?.NumberReference?.NumberingCache ?? (OpenXmlElement?)values?.NumberLiteral;
    return cache is null
        ? []
        : [.. cache.ChildElements.Where(child => child.LocalName == "pt").Select(NumberPoint).Where(point => point is not null).Select(point => point!.Value)];
  }

  private static int ValueCount(OpenXmlElement series) => Values(series).Count;

  private static string FirstPoint(OpenXmlElement cache)
    => cache.ChildElements.Where(child => child.LocalName == "pt").Select(PointText).FirstOrDefault() ?? string.Empty;

  private static string PointText(OpenXmlElement point)
    => point.ChildElements.FirstOrDefault(child => child.LocalName == "v")?.InnerText ?? string.Empty;

  private static double? NumberPoint(OpenXmlElement point)
    => double.TryParse(PointText(point), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

  private static string DefaultSeriesName(int position) => "Series " + (position + 1).ToString(CultureInfo.InvariantCulture);

  private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

  /// <summary>Writes the plan into the chart's embedded workbook: the series names across
  ///     the first row, the category names down the first column and the values beneath
  ///     them, which is the layout the chart's own formulas address. The sheet is rebuilt
  ///     as a whole, so a removed data point leaves no stale row behind.</summary>
  private static void WriteWorkbook(EmbeddedPackagePart workbook, PptxChartPlan plan)
  {
    using MemoryStream buffer = new();
    using (Stream source = workbook.GetStream())
    {
      source.CopyTo(buffer);
    }

    buffer.Position = 0;
    using (SpreadsheetDocument document = SpreadsheetDocument.Open(buffer, true))
    {
      WorksheetPart sheet = document.WorkbookPart?.WorksheetParts.FirstOrDefault()
          ?? throw new OpenXmlPackageException("the embedded workbook carries no worksheet");
      Fill(sheet, plan);
    }

    buffer.Position = 0;
    workbook.FeedData(buffer);
  }

  /// <summary>Rebuilds the workbook's first sheet from the plan and saves it.
  ///     </summary>
  private static void Fill(WorksheetPart sheet, PptxChartPlan plan)
  {
    S.Worksheet worksheet = sheet.Worksheet ?? throw new OpenXmlPackageException("the embedded workbook carries no worksheet");
    S.SheetData data = worksheet.GetFirstChild<S.SheetData>() ?? worksheet.AppendChild(new S.SheetData());
    data.RemoveAllChildren();
    data.Append(HeaderRow(plan.SeriesNames));
    for (int position = 0; position < plan.CategoryNames.Count; position++)
    {
      data.Append(DataRow(position, plan));
    }
  }

  private static S.Row HeaderRow(IReadOnlyList<string> names)
  {
    S.Row row = new() { RowIndex = 1U };
    for (int position = 0; position < names.Count; position++)
    {
      row.Append(TextCell(Column(position + 1) + "1", names[position]));
    }

    return row;
  }

  private static S.Row DataRow(int position, PptxChartPlan plan)
  {
    uint index = (uint)(position + 2);
    S.Row row = new() { RowIndex = index };
    row.Append(TextCell("A" + index.ToString(CultureInfo.InvariantCulture), plan.CategoryNames[position]));
    for (int series = 0; series < plan.Values.Count; series++)
    {
      row.Append(NumberCell(Column(series + 1) + index.ToString(CultureInfo.InvariantCulture), plan.Values[series][position]));
    }

    return row;
  }

  private static S.Cell TextCell(string reference, string text)
    => new() { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) };

  private static S.Cell NumberCell(string reference, double value)
    => new() { CellReference = reference, CellValue = new S.CellValue(Number(value)) };

  /// <summary>The column letter a 1-based series index writes into: column 1 is A, which
  ///     the category names occupy, so series N writes into column N+1.</summary>
  private static string Column(int series) => ((char)('A' + series)).ToString();

  private static Result<PptxResolvedEdit> Failed(string code, string message)
    => Result.Failure<PptxResolvedEdit>(new DomainError(code, message));

  private static Result<PptxChartTarget> Failure(string code, string message)
    => Result.Failure<PptxChartTarget>(new DomainError(code, message));

  private static Result<PptxChartPlan> PlanFailure(string code, string message)
    => Result.Failure<PptxChartPlan>(new DomainError(code, message));

  private static Result<int> DimensionFailure(string code, string message)
    => Result.Failure<int>(new DomainError(code, message));
}
