global using Xunit;

// Disable xUnit parallel test execution: VirtualTapeFixture instances (virtual tape drives
//  with 200 MB+ memory-backed media) cause contention when multiple test classes run concurrently.
//  Each class runs sequentially; individual tests within a class are always sequential in xUnit.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
