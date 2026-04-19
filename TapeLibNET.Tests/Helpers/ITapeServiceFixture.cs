using Grpc.Net.Client;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Common interface for tape service test fixtures, abstracting whether the gRPC
/// server is hosted in-process (<see cref="LocalHostTapeServiceFixture"/>) or
/// reached at an external address (<see cref="RemoteHostTapeServiceFixture"/>).
/// </summary>
public interface ITapeServiceFixture
{
    /// <summary>The gRPC channel connected to the tape service.</summary>
    GrpcChannel Channel { get; }

    /// <summary>The base address of the tape service (e.g. <c>http://127.0.0.1:50551</c>).</summary>
    string Address { get; }
}
