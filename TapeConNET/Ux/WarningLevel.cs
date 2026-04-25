// WarningLevel is now a project-level alias for ServiceReportLevel in TapeLibNET.Services.
// This preserves all existing call sites in TapeConNET unchanged while eliminating
// the duplicate enum definition.
global using WarningLevel = TapeLibNET.Services.ServiceReportLevel;
