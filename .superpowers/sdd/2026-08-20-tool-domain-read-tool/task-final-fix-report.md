# Final Review Fix Report

## Fixes

- **DI disposal:** `Program.Main` now uses a `using` scope for the built `ServiceProvider`, preserving both redirected and interactive REPL handler signatures while disposing singleton resources on normal exit.
- **File error classification:** `PowerShellFileSystemAccess` now opens the path directly and maps `FileNotFoundException`/`DirectoryNotFoundException` to `FileNotFound`, while mapping unauthorized and other I/O failures to `FileSystemError`. C# now consumes the script error code/message.
- **Provider payload hardening:** `OpenRouterModelProvider` now converts malformed successful response JSON, missing/empty choices, missing message fields, and malformed tool calls into `ProviderError` results. Existing HTTP and timeout mappings and success serialization remain unchanged.

## Tests Added

- Provider regression theory covering invalid JSON, missing choices, empty choices, missing message, and malformed `tool_calls`.
- File-system regression test covering a directory path returning `FileSystemError`.
- Existing missing-file test continues to verify `FileNotFound`.

## Verification

- `dotnet build eThangAgent.slnx --nologo` — succeeded, 0 warnings, 0 errors.
- `dotnet test eThangAgent.slnx --nologo -v q` — passed, 134 tests, 0 failures, 0 skipped.

## Commit

`b52eeef` (`fix: address final review findings (dispose DI, file error classification, provider payload hardening)`).
