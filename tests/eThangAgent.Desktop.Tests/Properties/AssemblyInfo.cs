using System.Runtime.Versioning;

// The desktop app under test is Windows-only by design (AGENTS.md) — its whole
// surface carries that declaration, so the test assembly must too.
[assembly: SupportedOSPlatform("windows")]
