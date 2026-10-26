using Xunit;

// The leak guard (issue #284 item 3). Every test in this suite opens a real session, and a
// session owns a real ChildHost process: without this fixture a leaked host degrades the run
// silently and holds its SQLite file.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.ChildHostLeakGuardFixture))]
