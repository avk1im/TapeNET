global using Xunit;

// Set to "true" to disable xUnit parallel test execution: VirtualTapeFixture instances (virtual tape drives
//  with 200 MB+ memory-backed media) may ause contention when multiple test classes run concurrently.
//  Each class runs sequentially; individual tests within a class are always sequential in xUnit.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
