using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

public class ServiceCalibrationTests : ServiceTestBase
{
    private const long MB = 1024L * 1024;
    private const long CalibrationCapacity = 64L * MB;

    private static async Task<(TapeServiceBase service, TestTapeServiceHost host)> OpenCalibrationServiceAsync(
        long capacity = CalibrationCapacity,
        VirtualTapeDriveIoRate? ioRate = null,
        VirtualTapeEwProfile? ewProfile = null)
    {
        var (service, host) = CreateService();

        var vmd = new VirtualMediaDescriptor("memory-calibration", capacity, null, 0, InMemory: true);

        Assert.True(await service.OpenVirtualDriveAsync(
                VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
                vmd,
                ioRate: ioRate,
                ewProfile: ewProfile ?? VirtualTapeEwProfile.Lto4Like(capacity)),
            $"OpenVirtualDriveAsync failed: {service.LastError}");

        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync failed: {service.LastError}");

        return (service, host);
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_ReturnsCalibrationAndLogsSummary()
    {
        var (service, host) = await OpenCalibrationServiceAsync();
        using (service)
        {
            var result = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(
                    EjectWhenDone: false,
                    Options: new TapeCalibrationOptions
                    {
                        SampleCount = 40,
                        //MinSampleInterval = 1L * MB,
                        //ChunkBytesTarget = 1L * MB,
                    }));

            Assert.True(result.Success);
            Assert.False(result.WasAborted);
            Assert.NotNull(result.Calibration);
            Assert.Equal(service.DriveProfileKey, result.ProfileKey);
            Assert.True(result.ReportedCapacityTotal > result.CapacityActual);
            Assert.True(result.PhantomFreeAtEom > 0);
            Assert.True(result.CapacityActual > 0);
            Assert.True(result.EwToEomDistance > 0);
            Assert.Contains(ServiceStateChange.OperationStarted, host.StateChanges);
            Assert.Contains(ServiceStateChange.OperationEnded, host.StateChanges);
            Assert.True(host.ContainsMessage("Calibration summary"));
            Assert.True(host.ContainsMessage("Calibration completed successfully"));
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_HonorsAbortRequest()
    {
        var (service, host) = await OpenCalibrationServiceAsync(
            capacity: 256L * MB,
            ioRate: new VirtualTapeDriveIoRate { BytesPerSecond = 8L * MB });

        using (service)
        using (var cts = new CancellationTokenSource())
        {
            var task = service.ExecuteCalibrateAsync(
                new CalibrateRequest(
                    EjectWhenDone: false,
                    Options: new TapeCalibrationOptions
                    {
                        SampleCount = 16,
                        //MinSampleInterval = 1L * MB,
                        //ChunkBytesTarget = 1L * MB,
                    })
                {
                    Cancellation = cts.Token,
                });

            await Task.Delay(100);
            cts.Cancel();

            var result = await task;
            Assert.True(result.WasAborted);
            Assert.False(result.Success);
            Assert.Null(result.Calibration);
            Assert.True(host.ContainsMessage("Calibration abort requested"));
        }
    }

    [Fact]
    public async Task ExecuteCalibrateAsync_WithCustomOverreport_ExposesBothOverreportAnchors()
    {
        var (service, _) = await OpenCalibrationServiceAsync(
            ewProfile: VirtualTapeEwProfile.Lto4Like(
                CalibrationCapacity, ewZonePercent: 4.0,
                phantomFreePercent: 10.0, reportedBoostPercent: 5.0));

        using (service)
        {
            var result = await service.ExecuteCalibrateAsync(
                new CalibrateRequest(
                    EjectWhenDone: false,
                    Options: new TapeCalibrationOptions
                    {
                        SampleCount = 20,
                        //MinSampleInterval = 1L * MB,
                        //ChunkBytesTarget = 1L * MB,
                    }));

            Assert.True(result.Success);
            // (a) capacity inflated at BOM, and (b) phantom free space still claimed at hard EOM.
            Assert.True(result.ReportedCapacityAtBom > result.CapacityActual);
            Assert.True(result.PhantomFreeAtEom > 0);
            Assert.NotNull(result.Calibration);
            Assert.True(result.Calibration!.Curve[0].ReportedRemaining > 0);
            Assert.Equal(0L, result.Calibration.Curve[0].ActualRemaining);
        }
    }
}
