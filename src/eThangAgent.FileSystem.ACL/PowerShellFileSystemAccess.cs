using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class PowerShellFileSystemAccess : IFileSystemAccess, IDisposable
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
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        _runspace.Open();
    }

    public async Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = PowerShell.Create(_runspace);
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

    public void Dispose()
    {
        _runspace.Dispose();
        _gate.Dispose();
    }
}
