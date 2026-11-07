using Xunit;

// The run's temp root (issue #371): every scratch file, workspace and database this
// assembly creates lands under one directory, and the fixture removes that directory
// wholesale when the last test finishes. A root it cannot remove fails the assembly
// instead of leaving debris nothing reclaims.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.TestTempRootFixture))]
