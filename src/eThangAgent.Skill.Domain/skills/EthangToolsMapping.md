---
name: ethang-tools-mapping
description: How superpowers action names bind to real eThang Agent tools.
---

# eThang Agent Tool Mapping

Skills name actions; this harness binds them to real tools:

| Action (as named by skills) | Binding |
| --- | --- |
| Read a file | `read` |
| Write / edit files | `write` / `edit` |
| Search files | `search_files` |
| Run shell commands / tests / git plumbing | `exec` (PowerShell) |
| Dispatch a subagent | spawn sub-agent capability |
| Create/update todos | `todo` tool |
| Invoke a skill / load its content | `skill_view` tool (never read raw skill paths; the skill store IS the mechanism) |
| List available skills | `skill_list` tool |
| Ask the human partner a clarifying question | `clarify` tool (MANDATORY for brainstorming) |
| Track plan progress | `todo` tool plus plan-file checkboxes |
| Commit work | `git_commit` tool (never raw shell commit) |

All scripts are PowerShell (.ps1). Windows-native. Tests: xUnit via dotnet test.
