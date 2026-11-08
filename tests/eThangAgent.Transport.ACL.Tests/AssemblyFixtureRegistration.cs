
// The assembly-level ChildHost leak guard (issue #377): this assembly opens sessions, and a
// session OWNS a host process, so a test that throws before its teardown leaves a real
// ChildHost running. Without the guard that survivor is invisible to the run and surfaces only
// as the temp-root sweep failing minutes later - reported against every test in the assembly
// rather than against the one that leaked. The guard is ancestry-based, so it examines THIS
// process's children only.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.ChildHostLeakGuardFixture))]

// The run's temp root (issue #371): every scratch file, workspace and database this
// assembly creates lands under one directory, and the fixture removes that directory
// wholesale when the last test finishes. A root it cannot remove fails the assembly
// instead of leaving debris nothing reclaims.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.TestTempRootFixture))]
