// The run's temp root (issue #371): every scratch file, workspace and database this
// assembly creates lands under one directory, and the fixture removes that directory
// wholesale when the last test finishes. A root it cannot remove fails the assembly
// instead of leaving debris nothing reclaims.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.TestTempRootFixture))]

// The real-input leak guard: a PipeServer built with zero send hooks resolves the REAL
// NativeInput surface, so a test that dispatches an input method through one types into the
// focused window and scrolls under the cursor. The guard snapshots NativeInput's injection
// counter at assembly start and fails the run if anything injected.
[assembly: AssemblyFixture(typeof(eThangAgent.ComputerUse.Host.Tests.RealInputLeakGuardFixture))]
