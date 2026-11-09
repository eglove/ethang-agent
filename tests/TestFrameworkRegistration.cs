// Linked into every test project by tests/Directory.Build.props, so the ceiling is registered
// once for the whole suite rather than per project: a new test project cannot join without it.
// It lives outside any project directory because it belongs to none of them - it is the suite's
// registration, and a copy per project would be a copy that can drift.
using eThangAgent.TestSupport;

// The attribute is named in full because this file is compiled into every test project, and not
// all of them import the Xunit namespace. Naming it in full is what makes one file valid in all
// of them, so the simplification the style rule suggests is the one thing that must not happen.
#pragma warning disable IDE0001 // Name can be simplified
[assembly: Xunit.TestFramework(typeof(CeilingTestFramework))]
#pragma warning restore IDE0001
