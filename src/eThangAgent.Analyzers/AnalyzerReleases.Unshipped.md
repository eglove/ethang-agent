; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ETA0001 | Reliability | Warning | TimedWaitAnalyzer, wall-clock bound that is not the test ceiling
ETA0002 | Reliability | Warning | TimedWaitAnalyzer, fixed-duration wait in test code
