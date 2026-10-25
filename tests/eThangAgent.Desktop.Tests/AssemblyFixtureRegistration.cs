// The assembly-level ChildHost leak guard (issue #284 item 3). The desktop suite opens
// real sessions through the composed core, so a test that throws before its teardown
// would strand a ChildHost exactly as it did in the composition suite. The guard is
// ancestry-based: it examines THIS process's children only, so the harness's own host
// (a different process tree) can never trip it.
[assembly: AssemblyFixture(typeof(eThangAgent.TestSupport.ChildHostLeakGuardFixture))]
