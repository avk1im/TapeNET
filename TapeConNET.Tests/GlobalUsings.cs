global using Xunit;

// WarningLevel is a project-level alias for ServiceReportLevel in TapeLibNET.Services,
//  mirroring the same alias defined in the main TapeConNET project.
global using WarningLevel = TapeLibNET.Services.ServiceReportLevel;

// Tests within a class always run sequentially in xUnit; classes themselves
//  may run in parallel. Each test creates its own virtual drive, so cross-class
//  contention is limited to disk I/O on the temp directory — fine to leave on.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
