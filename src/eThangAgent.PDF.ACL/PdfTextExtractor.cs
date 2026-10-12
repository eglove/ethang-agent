using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace eThangAgent.PDF.ACL;

/// <summary>The read path over PdfPig (task 5): stats the file FIRST for the
///     content-true <see cref="FileVersion"/> (issue #117 pattern), gates the open
///     through <see cref="PdfFormatSniffer"/>, then extracts one 1-based page slice of
///     text plus the document metadata. Pages are 1-based; the offset is the 0-based
///     first page, so offset 0 names page 1. An offset at or past the
///     page count refuses with PageNotFound naming the count - never a clamp. Library
///     faults map to the canonical codes; unknown failures become CorruptPdf with the
///     library message clamped to ~300 characters.</summary>
public static class PdfTextExtractor
{
  /// <summary>Extracts the page slice and metadata, or a typed <see cref="DomainError"/>.</summary>
  public static Result<PdfSnapshot> Extract(string path, int offset, int maxPages)
  {
    ArgumentNullException.ThrowIfNull(path);
    if (offset < 0 || maxPages < 1)
    {
      return Fail(ToolErrorCodes.InvalidParameterValue,
          $"offset must be >= 0 and maxPages must be >= 1; got offset {offset}, maxPages {maxPages}.");
    }

    if (!File.Exists(path))
    {
      return Fail(PdfErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    // Stat FIRST: the version is the pair captured before the document is opened.
    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);

    Result<string> sniff = PdfFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<PdfSnapshot>(sniff.Error);
    }

    try
    {
      using PdfDocument doc = PdfDocument.Open(path);
      int totalPages = doc.NumberOfPages;
      if (offset >= totalPages)
      {
        return Fail(PdfErrorCodes.PageNotFound,
            $"Page offset {offset} is out of range; the document has {totalPages} page(s).");
      }

      int take = Math.Min(maxPages, totalPages - offset);
      List<PdfPageText> pages = new(take);
      for (int i = 0; i < take; i++)
      {
        int pageNumber = offset + i + 1;
        Page page = doc.GetPage(pageNumber);
        pages.Add(new PdfPageText(pageNumber, page.Width, page.Height, SplitLines(page.Text)));
      }

      return Result.Success(new PdfSnapshot(
          version, totalPages, offset, pages, doc.Information?.Title, doc.Information?.Author));
    }
    catch (PdfDocumentEncryptedException ex)
    {
      return Fail(PdfErrorCodes.EncryptedPdf, $"PDF file '{path}' is encrypted: {Clamp(ex.Message)}");
    }
#pragma warning disable CA1031 // Named decision: the open/extract boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"'{path}' could not be read as a PDF: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Splits PdfPig page text into lines on '\n' and '\r', dropping trailing
  ///     empty lines (a trailing newline is not a line).</summary>
  private static List<string> SplitLines(string text)
  {
    List<string> lines = [.. text.Split(['\n', '\r'], StringSplitOptions.None)];
    int end = lines.Count;
    while (end > 0 && lines[end - 1].Length == 0)
    {
      end--;
    }

    lines.RemoveRange(end, lines.Count - end);
    return lines;
  }

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PdfSnapshot> Fail(string code, string message)
      => Result.Failure<PdfSnapshot>(new DomainError(code, message));
}
