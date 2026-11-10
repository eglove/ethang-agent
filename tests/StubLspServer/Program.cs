using FakeLspServer;

// A minimal Language Server over stdio. It exists so a test can observe WHICH PROCESS
// answered a request: a starter faked inside the daemon produces no second process, so
// 'two servers served two languages' would be an assertion about the test's own fake.
using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();
return await FakeServer.RunAsync(input, output).ConfigureAwait(false);
