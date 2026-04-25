global using Xunit;

// Tests within a class always run sequentially in xUnit; classes themselves
//  may run in parallel. Each test creates its own virtual drive, so cross-class
//  contention is limited to disk I/O on the temp directory — fine to leave on.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
