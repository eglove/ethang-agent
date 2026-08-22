using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>
/// Read-only git queries and index-only commits over a single open runspace,
/// mirroring <see cref="PowerShellFileSystemAccess"/> construction and gating exactly.
/// Every operation shells out to git with <c>-C</c> anchored at the repository root,
/// so no working-directory juggling takes place.
/// </summary>
public sealed class PowerShellGitAccess : IGitQueryAccess, IGitCommitAccess, IDisposable
{
    private const int MaxPatchChars = 20000;

    // Runs git with stdout/stderr captured separately (exact exit codes, no stream
    // merging, no CRLF rewriting). Returns ExitCode / StdOut / StdErr.
    private const string InvokeGitHelper = """
        function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = 'git'
            [void]$psi.ArgumentList.Add('-C')
            [void]$psi.ArgumentList.Add($Dir)
            foreach ($a in $GitArgs) { [void]$psi.ArgumentList.Add($a) }
            $psi.WorkingDirectory = $Dir
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.UseShellExecute = $false
            $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
            $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
            $p = [System.Diagnostics.Process]::Start($psi)
            $errTask = $p.StandardError.ReadToEndAsync()
            $out = $p.StandardOutput.ReadToEnd()
            $err = $errTask.GetAwaiter().GetResult()
            $p.WaitForExit()
            return @{ ExitCode = $p.ExitCode; StdOut = $out; StdErr = $err }
        }
        function ConvertTo-GitFailure([hashtable]$Result, [string]$Root) {
            $stderr = $Result['StdErr']
            if ($stderr -match 'not a git repository') {
                return @{ Ok = $false; ErrorCode = 'NotAGitRepository';
                          ErrorMessage = "Not a git repository: $Root" }
            }
            return @{ Ok = $false; ErrorCode = 'GitError'; ErrorMessage = $stderr.Trim() }
        }
        """;

    private const string StatusParams = """
        param([string]$Root)

        """;

    private const string StatusScript = StatusParams + InvokeGitHelper + """

        $branchRes = Invoke-Git $Root @('rev-parse', '--abbrev-ref', 'HEAD')
        if ($branchRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $branchRes $Root
        }
        $branch = $branchRes['StdOut'].Trim()

        $statusRes = Invoke-Git $Root @('status', '--porcelain')
        if ($statusRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $statusRes $Root
        }

        $staged = [System.Collections.Generic.List[object]]::new()
        $unstaged = [System.Collections.Generic.List[object]]::new()
        $untracked = [System.Collections.Generic.List[string]]::new()

        $lines = $statusRes['StdOut'] -split "`r?`n"
        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) { continue }
            $code = $line.Substring(0, 2)
            $path = $line.Substring(3)
            if ($code -eq '??') {
                [void]$untracked.Add($path)
                continue
            }
            # Renames keep their FULL 'old -> new' porcelain string as the Path.
            $x = $code.Substring(0, 1)
            $y = $code.Substring(1, 1)
            if ($x -ne ' ') { [void]$staged.Add(@{ Code = $code; Path = $path }) }
            if ($y -ne ' ') { [void]$unstaged.Add(@{ Code = $code; Path = $path }) }
        }

        return @{ Ok = $true; Branch = $branch; Staged = $staged;
                  Unstaged = $unstaged; Untracked = $untracked }
        """;

    private const string DiffParams = """
        param([string]$Root, [string]$Scope, [int]$Cap, [string]$Path)

        """;

    private const string DiffScript = DiffParams + InvokeGitHelper + """

        $probe = Invoke-Git $Root @('rev-parse', '--git-dir')
        if ($probe['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $probe $Root
        }

        $wantStaged = $Scope -in @('Staged', 'All')
        $wantUnstaged = $Scope -in @('Unstaged', 'All')

        # Optional pathspec filter: everything after '--'.
        $pathArgs = @()
        if ($Path -ne '') { $pathArgs = @('--', $Path) }

        $files = 0; $additions = 0; $deletions = 0
        $numstatLines = @()
        if ($wantStaged) {
            $r = Invoke-Git $Root (@('diff', '--cached', '--numstat') + $pathArgs)
            if ($r['ExitCode'] -ne 0) { return ConvertTo-GitFailure $r $Root }
            $numstatLines += ($r['StdOut'] -split "`r?`n")
        }
        if ($wantUnstaged) {
            $r = Invoke-Git $Root (@('diff', '--numstat') + $pathArgs)
            if ($r['ExitCode'] -ne 0) { return ConvertTo-GitFailure $r $Root }
            $numstatLines += ($r['StdOut'] -split "`r?`n")
        }
        foreach ($row in $numstatLines) {
            if ([string]::IsNullOrWhiteSpace($row)) { continue }
            $parts = $row.Split("`t")
            if ($parts.Count -lt 3) { continue }
            $files++
            if ($parts[0] -ne '-') { $additions += [int]$parts[0] } # binary '-' counts as 0
            if ($parts[1] -ne '-') { $deletions += [int]$parts[1] } # binary '-' counts as 0
        }

        $sb = [System.Text.StringBuilder]::new()
        if ($wantStaged) {
            $r = Invoke-Git $Root (@('diff', '--cached') + $pathArgs)
            if ($r['ExitCode'] -ne 0) { return ConvertTo-GitFailure $r $Root }
            if ($r['StdOut'] -ne '') {
                [void]$sb.Append('### staged ###').Append("`n").Append($r['StdOut'])
            }
        }
        if ($wantUnstaged) {
            $r = Invoke-Git $Root (@('diff') + $pathArgs)
            if ($r['ExitCode'] -ne 0) { return ConvertTo-GitFailure $r $Root }
            if ($r['StdOut'] -ne '') {
                if ($sb.Length -gt 0) { [void]$sb.Append("`n") }
                [void]$sb.Append('### unstaged ###').Append("`n").Append($r['StdOut'])
            }
        }
        $patch = $sb.ToString()

        # Bound the patch at $Cap characters, cutting at the last complete line before
        # the cap. TotalChars always reports the FULL untruncated length.
        $totalChars = $patch.Length
        $truncated = $false
        if ($totalChars -gt $Cap) {
            $cut = $patch.LastIndexOf("`n", $Cap - 1)
            if ($cut -lt 0) { $cut = $Cap - 1 }
            $patch = $patch.Substring(0, $cut + 1)
            $truncated = $true
        }

        return @{ Ok = $true; Files = $files; Additions = $additions; Deletions = $deletions;
                  Patch = $patch; Truncated = $truncated; TotalChars = $totalChars }
        """;

    private const string CommitParams = """
        param([string]$Root, [string]$Message)

        """;

    private const string CommitScript = CommitParams + InvokeGitHelper + """

        # Outside a repository 'git diff --cached' exits 129 with usage text, so
        # probe repo-ness explicitly first.
        $probe = Invoke-Git $Root @('rev-parse', '--git-dir')
        if ($probe['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $probe $Root
        }

        $indexRes = Invoke-Git $Root @('diff', '--cached', '--name-only')
        if ($indexRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $indexRes $Root
        }
        if ([string]::IsNullOrWhiteSpace($indexRes['StdOut'])) {
            return @{ Ok = $false; ErrorCode = 'NothingStaged';
                      ErrorMessage = "The index is empty; there is nothing to commit in $Root. Stage changes first (e.g. exec: git add <file>)." }
        }

        # Commit the CURRENT INDEX via a temp message file so multi-line messages
        # survive verbatim. Never stages anything itself.
        $tmp = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($tmp, $Message, [System.Text.UTF8Encoding]::new($false))
            $commitRes = Invoke-Git $Root @('commit', '-F', $tmp)
        } finally {
            [System.IO.File]::Delete($tmp)
        }
        if ($commitRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $commitRes $Root
        }

        $hashRes = Invoke-Git $Root @('rev-parse', '--short', 'HEAD')
        if ($hashRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $hashRes $Root
        }
        $branchRes = Invoke-Git $Root @('rev-parse', '--abbrev-ref', 'HEAD')
        if ($branchRes['ExitCode'] -ne 0) {
            return ConvertTo-GitFailure $branchRes $Root
        }

        return @{ Ok = $true; Hash = $hashRes['StdOut'].Trim();
                  Branch = $branchRes['StdOut'].Trim(); Message = $Message }
        """;

    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellGitAccess()
    {
        _runspace = RunspaceHost.CreateOpen();
    }

    public async Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(StatusScript)
              .AddParameter("Root", repoPath);
            var output = ps.Invoke();
            if (ps.HadErrors || output.Count == 0)
                return Result<GitStatus>.Failure(new Error("GitError",
                    ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                        ?? "PowerShell script produced no output."));
            var table = (Hashtable)output[0].BaseObject;
            if (table["Ok"] is not true)
                return Result<GitStatus>.Failure(new Error(
                    table["ErrorCode"]?.ToString() ?? "GitError",
                    table["ErrorMessage"]?.ToString() ?? "Unknown git error."));

            var staged = ReadEntries(table["Staged"]);
            var unstaged = ReadEntries(table["Unstaged"]);
            var untracked = ((IEnumerable)table["Untracked"]!).Cast<object>()
                .Select(o => o is PSObject po ? po.BaseObject?.ToString() ?? "" : o?.ToString() ?? "")
                .ToList();

            return Result<GitStatus>.Success(new GitStatus(
                table["Branch"]?.ToString() ?? "",
                staged,
                unstaged,
                untracked));
        }
        finally { _gate.Release(); }
    }

    public async Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
    {
        if (scope is not ("Staged" or "Unstaged" or "All"))
            return Result<GitDiff>.Failure(new Error("InvalidScope",
                $"Unknown diff scope '{scope}'. Expected 'Staged', 'Unstaged', or 'All'."));
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(DiffScript)
              .AddParameter("Root", repoPath)
              .AddParameter("Scope", scope)
              .AddParameter("Cap", MaxPatchChars)
              .AddParameter("Path", path ?? string.Empty);
            var output = ps.Invoke();
            if (ps.HadErrors || output.Count == 0)
                return Result<GitDiff>.Failure(new Error("GitError",
                    ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                        ?? "PowerShell script produced no output."));
            var table = (Hashtable)output[0].BaseObject;
            if (table["Ok"] is not true)
                return Result<GitDiff>.Failure(new Error(
                    table["ErrorCode"]?.ToString() ?? "GitError",
                    table["ErrorMessage"]?.ToString() ?? "Unknown git error."));

            var stats = new GitDiffStats(
                Convert.ToInt32(table["Files"]!),
                Convert.ToInt32(table["Additions"]!),
                Convert.ToInt32(table["Deletions"]!));

            return Result<GitDiff>.Success(new GitDiff(
                stats,
                table["Patch"]?.ToString() ?? "",
                table["Truncated"] is true,
                Convert.ToInt32(table["TotalChars"]!)));
        }
        finally { _gate.Release(); }
    }

    public async Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(CommitScript)
              .AddParameter("Root", repoPath)
              .AddParameter("Message", message);
            var output = ps.Invoke();
            if (ps.HadErrors || output.Count == 0)
                return Result<GitCommitOutcome>.Failure(new Error("GitError",
                    ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                        ?? "PowerShell script produced no output."));
            var table = (Hashtable)output[0].BaseObject;
            if (table["Ok"] is not true)
                return Result<GitCommitOutcome>.Failure(new Error(
                    table["ErrorCode"]?.ToString() ?? "GitError",
                    table["ErrorMessage"]?.ToString() ?? "Unknown git error."));

            return Result<GitCommitOutcome>.Success(new GitCommitOutcome(
                table["Hash"]?.ToString() ?? "",
                table["Branch"]?.ToString() ?? "",
                table["Message"]?.ToString() ?? ""));
        }
        finally { _gate.Release(); }
    }

    private static List<GitStatusEntry> ReadEntries(object? rawList)
    {
        var entries = new List<GitStatusEntry>();
        foreach (var raw in (IEnumerable)rawList!)
        {
            var ht = (Hashtable)(raw is PSObject pso ? pso.BaseObject! : raw);
            entries.Add(new GitStatusEntry(
                ht["Code"]?.ToString() ?? "",
                ht["Path"]?.ToString() ?? ""));
        }
        return entries;
    }

    public void Dispose()
    {
        _runspace.Dispose();
        _gate.Dispose();
    }
}
