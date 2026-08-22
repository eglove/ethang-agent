using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class PowerShellFileSystemAccess : IFileSystemAccess, IFileWriteAccess, IFileEditAccess, IDisposable
{
    private const string Script = """
        param([string]$Path, [int]$Start, [int]$End)
        try {
            $reader = [System.IO.File]::OpenText($Path)
        } catch [System.IO.FileNotFoundException] {
            return @{ Found = $false; ErrorCode = "FileNotFound" }
        } catch [System.IO.DirectoryNotFoundException] {
            return @{ Found = $false; ErrorCode = "FileNotFound" }
        } catch [System.UnauthorizedAccessException] {
            return @{ Found = $false; ErrorCode = "FileSystemError"; ErrorMessage = $_.Exception.Message }
        } catch [System.IO.IOException] {
            return @{ Found = $false; ErrorCode = "FileSystemError"; ErrorMessage = $_.Exception.Message }
        }
        try {
            $lines = [System.Collections.Generic.List[string]]::new()
            $i = 0; $last = 0
            while ($true) {
                $line = $reader.ReadLine()
                if ($null -eq $line) { break }
                $i++
                if ($i -ge $Start) { [void]$lines.Add($line); $last = $i }
                if ($i -ge $End) {
                    # Drain remaining lines to count total lines accurately
                    while ($null -ne $reader.ReadLine()) { $i++ }
                    break
                }
            }
            return @{ Found = $true; Lines = $lines; LastLine = $last; TotalLines = $i }
        } finally { $reader.Dispose() }
        """;

    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellFileSystemAccess()
    {
        _runspace = RunspaceHost.CreateOpen();
    }

    public async Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(Script)
              .AddParameter("Path", path)
              .AddParameter("Start", startLine)
              .AddParameter("End", endLine);

            var output = ps.Invoke();

            if (ps.HadErrors)
            {
                var msg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                    ?? "Unknown PowerShell error.";
                return Result<FileRead>.Failure(new Error("FileSystemError", msg));
            }
            if (output.Count == 0)
                return Result<FileRead>.Failure(new Error("FileSystemError",
                    "PowerShell script produced no output."));

            var table = (Hashtable)output[0].BaseObject;
            var found = table["Found"] is true;
            if (!found)
            {
                var errorCode = table["ErrorCode"]?.ToString() ?? "FileSystemError";
                var errorMessage = table["ErrorMessage"]?.ToString()
                    ?? $"File not found: {path}";
                return Result<FileRead>.Failure(new Error(errorCode, errorMessage));
            }

            var rawLines = (IEnumerable)table["Lines"]!;
            var lines = rawLines.Cast<object>()
                .Select(o => o is PSObject pso ? pso.BaseObject?.ToString() ?? "" : o.ToString() ?? "")
                .ToList();
            var lastLine = Convert.ToInt32(table["LastLine"]);
            var totalLines = Convert.ToInt32(table["TotalLines"]);

            return Result<FileRead>.Success(new FileRead(lines, lastLine, totalLines));
        }
        finally
        {
            _gate.Release();
        }
    }

    private const string WriteScript = """
        param([string]$Path, [string]$Content, [bool]$Overwrite)
        $exists = [System.IO.File]::Exists($Path)
        if ($exists -and -not $Overwrite) {
            return @{ Ok = $false; ErrorCode = "FileExists";
                      ErrorMessage = "File already exists: $Path (overwrite not requested)." }
        }
        $dir = [System.IO.Path]::GetDirectoryName($Path)
        if (-not [System.IO.Directory]::Exists($dir)) {
            return @{ Ok = $false; ErrorCode = "DirectoryNotFound";
                      ErrorMessage = "Parent directory does not exist: $dir." }
        }
        try {
            $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
            [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
            return @{ Ok = $true; Created = (-not $exists);
                      Bytes = (Get-Item -LiteralPath $Path).Length }
        } catch {
            return @{ Ok = $false; ErrorCode = "FileSystemError";
                      ErrorMessage = $_.Exception.Message }
        }
        """;

    public async Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(WriteScript)
              .AddParameter("Path", path)
              .AddParameter("Content", content)
              .AddParameter("Overwrite", overwrite);
            var output = ps.Invoke();
            if (ps.HadErrors || output.Count == 0)
                return Result<FileWriteOutcome>.Failure(new Error("FileSystemError",
                    ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                        ?? "PowerShell script produced no output."));
            var table = (Hashtable)output[0].BaseObject;
            if (table["Ok"] is not true)
                return Result<FileWriteOutcome>.Failure(new Error(
                    table["ErrorCode"]?.ToString() ?? "FileSystemError",
                    table["ErrorMessage"]?.ToString() ?? "Unknown filesystem error."));
            return Result<FileWriteOutcome>.Success(new FileWriteOutcome(
                table["Created"] is true, Convert.ToInt64(table["Bytes"]!)));
        }
        finally { _gate.Release(); }
    }

    private const string ReplaceScript = """
        param([string]$Path, [string]$Old, [string]$New, [int]$Occurrences, [bool]$All)
        if (-not [System.IO.File]::Exists($Path)) {
            return @{ Ok = $false; ErrorCode = "FileNotFound";
                      ErrorMessage = "File not found: $Path" }
        }
        try {
            $text = [System.IO.File]::ReadAllText($Path)
        } catch {
            return @{ Ok = $false; ErrorCode = "FileSystemError";
                      ErrorMessage = $_.Exception.Message }
        }
        if ($text.IndexOf([char]0) -ge 0) {
            return @{ Ok = $false; ErrorCode = "BinaryFile";
                      ErrorMessage = "File appears to be binary (NUL byte found): $Path. Use shell tools for binary files." }
        }
        $count = 0
        $idx = $text.IndexOf($Old, [System.StringComparison]::Ordinal)
        while ($idx -ge 0) { $count++; $idx = $text.IndexOf($Old, $idx + $Old.Length, [System.StringComparison]::Ordinal) }
        if ($count -eq 0) {
            return @{ Ok = $false; ErrorCode = "AnchorNotFound";
                      ErrorMessage = "Anchor text (length $($Old.Length)) not found in $Path." }
        }
        if (-not $All -and $count -ne $Occurrences) {
            return @{ Ok = $false; ErrorCode = "OccurrenceMismatch";
                      ErrorMessage = "Anchor occurs $count time(s) but $Occurrences replacement(s) were requested." }
        }
        $sb = [System.Text.StringBuilder]::new()
        $pos = 0; $done = 0
        $target = if ($All) { $count } else { $Occurrences }
        while ($done -lt $target) {
            $idx = $text.IndexOf($Old, $pos, [System.StringComparison]::Ordinal)
            [void]$sb.Append($text.Substring($pos, $idx - $pos))
            [void]$sb.Append($New)
            $pos = $idx + $Old.Length
            $done++
        }
        [void]$sb.Append($text.Substring($pos))
        $result = $sb.ToString()
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($Path, $result, $utf8NoBom)
        $lines = 0
        if ($result.Length -gt 0) {
            $lines = 1
            foreach ($ch in $result.ToCharArray()) { if ($ch -eq "`n") { $lines++ } }
        }
        return @{ Ok = $true; Replaced = $done; NewLineCount = $lines }
        """;

    public async Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(ReplaceScript)
              .AddParameter("Path", path)
              .AddParameter("Old", oldText)
              .AddParameter("New", newText)
              .AddParameter("Occurrences", occurrences ?? 0)
              .AddParameter("All", occurrences is null);
            var output = ps.Invoke();
            if (ps.HadErrors || output.Count == 0)
                return Result<ReplaceOutcome>.Failure(new Error("FileSystemError",
                    ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                        ?? "PowerShell script produced no output."));
            var table = (Hashtable)output[0].BaseObject;
            if (table["Ok"] is not true)
                return Result<ReplaceOutcome>.Failure(new Error(
                    table["ErrorCode"]?.ToString() ?? "FileSystemError",
                    table["ErrorMessage"]?.ToString() ?? "Unknown filesystem error."));
            return Result<ReplaceOutcome>.Success(new ReplaceOutcome(
                Convert.ToInt32(table["Replaced"]!), Convert.ToInt32(table["NewLineCount"]!)));
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _runspace.Dispose();
        _gate.Dispose();
    }
}
