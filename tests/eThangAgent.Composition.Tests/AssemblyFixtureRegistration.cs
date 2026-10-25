// The assembly-level ChildHost leak guard (issue #284 item 3), registered for the suite
// that opens sessions: a session OWNS a host process, so a test that throws before its
// teardown leaves a real ChildHost running, holding its SQLite file and silently slowing
// every later run (measured during #271: over 4 minutes with a leaked host alive, 1m15s
// once it was killed). The guard is ancestry-based - it examines THIS process's children
// only - so it cannot fire on the harness's own conversation host, which is a different
// process tree. See eThangAgent.Tests.Shared for the reasoning and the policy tests.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.ChildHostLeakGuardFixture))]
