// The assembly-level ChildHost leak guard (issue #284 item 3). The desktop suite opens
// real sessions through the composed core, so a test that throws before its teardown
// would strand a ChildHost exactly as it did in the composition suite. The guard is
// ancestry-based: it examines THIS process's children only, so the harness's own host
// (a different process tree) can never trip it.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.ChildHostLeakGuardFixture))]
// The same guard for the language-server daemon (issue #361). A daemon is a child of the
// process that started it and serves a named pipe until it is stopped, so a test that
// opens a code workspace and never disposes its host strands one per run.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.LspDaemonLeakGuardFixture))]

// The run's temp root (issue #371): every scratch file, workspace and database this
// assembly creates lands under one directory, and the fixture removes that directory
// wholesale when the last test finishes. A root it cannot remove fails the assembly
// instead of leaving debris nothing reclaims.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.TestTempRootFixture))]
