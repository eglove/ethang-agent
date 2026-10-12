using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PDF.ACL;

/// <summary>Decides whether a file may be opened as a PDF (task 4). Magic header bytes
///     (%PDF-) first, then trailer encryption heuristic. Success value is "pdf".
///     Callers catch and map library-specific open errors.</summary>
public static class PdfFormatSniffer
{
  private const int TrailerRegionBytes = 2048;

  /// <summary>Stats the file, checks the %PDF- magic header, and inspects the trailer region for encryption.
  ///     Returns "pdf" on success or a typed <see cref="DomainError"/> on failure.</summary>
  public static Result<string> Inspect(string path)
  {
    if (!File.Exists(path))
    {
      return Result.Failure<string>(new DomainError(PdfErrorCodes.FileNotFound,
          $"File not found: '{path}'."));
    }

    FileInfo info = new(path);
    if (info.Length > PdfLimits.MaxFileBytes)
    {
      return Result.Failure<string>(new DomainError(PdfErrorCodes.OversizePdf,
          $"File is {info.Length} bytes; the maximum is {PdfLimits.MaxFileBytes}."));
    }

    if (info.Length < 5)
    {
      return Result.Failure<string>(new DomainError(PdfErrorCodes.NotAPdf,
          $"'{path}' is not a PDF file (%PDF- header missing)."));
    }

    using FileStream fs = File.OpenRead(path);
    byte[] header = new byte[5];
    fs.ReadExactly(header);
    if (!header.AsSpan().SequenceEqual("%PDF-"u8))
    {
      return Result.Failure<string>(new DomainError(PdfErrorCodes.NotAPdf,
          $"'{path}' is not a PDF file (%PDF- header missing)."));
    }

    int trailerSize = (int)Math.Min(TrailerRegionBytes, info.Length);
    _ = fs.Seek(-trailerSize, SeekOrigin.End);
    byte[] trailer = new byte[trailerSize];
    fs.ReadExactly(trailer);
    return trailer.AsSpan().IndexOf("/Encrypt"u8) >= 0
      ? Result.Failure<string>(new DomainError(PdfErrorCodes.EncryptedPdf,
          $"PDF file '{path}' is encrypted (/Encrypt tag found in trailer region)."))
      : Result.Success("pdf");
  }
}
