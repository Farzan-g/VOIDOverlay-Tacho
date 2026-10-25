using System.Net.NetworkInformation;
using System.IO;
using System.Windows;

namespace VOIDOverlay.Tacho.Tests;

public sealed class CoreBehaviorTests
{
    [Theory]
    [InlineData(100, 1100, 1, 1000)]
    [InlineData(100, 200, 0.5, 200)]
    public void RateUsesCounterDeltaAndElapsedSeconds(ulong previous, ulong current, double seconds, double expected) =>
        Assert.Equal(expected, NetworkRateCalculator.BytesPerSecond(previous, current, seconds));

    [Theory]
    [InlineData(100, 99, 1)]
    [InlineData(0, 100, 0)]
    [InlineData(0, 100, -1)]
    [InlineData(0, 100, double.NaN)]
    public void RateRejectsResetAndInvalidElapsedTime(ulong previous, ulong current, double seconds) =>
        Assert.Null(NetworkRateCalculator.BytesPerSecond(previous, current, seconds));

    [Fact]
    public void AutomaticSelectionPrefersRoutedActiveAdapterAndHighestLinkSpeed()
    {
        var adapters = new[]
        {
            new InterfaceOption("slow", "Ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1_000, true, false),
            new InterfaceOption("fast", "Wi-Fi", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, 2_000, true, false),
            new InterfaceOption("vpn", "Tunnel", NetworkInterfaceType.Tunnel, OperationalStatus.Up, 10_000, true, true),
            new InterfaceOption("down", "Disconnected", NetworkInterfaceType.Ethernet, OperationalStatus.Down, 20_000, true, false),
            new InterfaceOption("local", "Virtual local", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 50_000, false, true)
        };

        Assert.Equal("fast", NetworkInterfaceSelector.SelectAutomatic(adapters)?.Id);
    }

    [Fact]
    public void AutomaticSelectionUsesActiveAdapterWhenNoDefaultRouteExists()
    {
        var adapters = new[]
        {
            new InterfaceOption("one", "One", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 10, false, false),
            new InterfaceOption("two", "Two", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 20, false, false)
        };

        Assert.Equal("two", NetworkInterfaceSelector.SelectAutomatic(adapters)?.Id);
    }

    [Fact]
    public void ManualSelectionCanSelectDisconnectedAdapter()
    {
        var adapter = new InterfaceOption("manual", "Manual", NetworkInterfaceType.Ethernet, OperationalStatus.Down, 100, false, true);
        Assert.Equal(adapter, NetworkInterfaceSelector.Select([adapter], "manual"));
    }

    [Fact]
    public void AutomaticSelectionAvoidsCommonVirtualAdaptersWhenPhysicalOptionsExist()
    {
        var adapters = new[]
        {
            new InterfaceOption("physical", "Ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1_000, true, false),
            new InterfaceOption("virtual", "Hyper-V Virtual Ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 10_000, true, true)
        };
        Assert.Equal("physical", NetworkInterfaceSelector.SelectAutomatic(adapters)?.Id);
    }

    [Fact]
    public async Task NetworkMonitorSamplesThroughInjectedProvider()
    {
        var settings = AppSettings.Defaults();
        var adapter = new InterfaceOption("fake", "Test adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1000, true, false);
        using var monitor = new NetworkMonitorService(settings, new IncrementingNetworkProvider(adapter));
        var sampleSource = new TaskCompletionSource<NetworkSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotAvailable += snapshot =>
        {
            if (snapshot.DownloadBytesPerSecond.HasValue && snapshot.UploadBytesPerSecond.HasValue)
                sampleSource.TrySetResult(snapshot);
        };
        monitor.RefreshSettings();

        var sample = await sampleSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sample.IsConnected);
        Assert.True(sample.DownloadBytesPerSecond > 0);
        Assert.True(sample.UploadBytesPerSecond > 0);
    }

    [Theory]
    [InlineData(24.0)]
    [InlineData(null)]
    public async Task PingMonitorUsesInjectedProbeAndPreservesUnavailableState(double? latencyMs)
    {
        var settings = AppSettings.Defaults();
        settings.PingEnabled = true;
        settings.PingIntervalMs = 60000;
        var provider = new FixedPingProvider(latencyMs);
        using var monitor = new PingMonitorService(settings, provider);
        var snapshotSource = new TaskCompletionSource<LatencySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotAvailable += snapshot => snapshotSource.TrySetResult(snapshot);
        monitor.RefreshSettings();

        var snapshot = await snapshotSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(latencyMs, snapshot.LatencyMs);
        Assert.Equal("1.1.1.1", provider.Target);
        Assert.Equal(2000, provider.TimeoutMs);
    }

    [Fact]
    public void TransferFormatterUsesDecimalUnitsConsistently()
    {
        Assert.Equal("999 B/s", TransferFormatter.Format(999, TransferUnit.BytesPerSecond));
        Assert.Equal("1.0 KB/s", TransferFormatter.Format(1000, TransferUnit.BytesPerSecond));
        Assert.Equal("1.0 Mbps", TransferFormatter.Format(125_000, TransferUnit.BitsPerSecond));
        Assert.Equal("—", TransferFormatter.Format(null, TransferUnit.BytesPerSecond));
        Assert.Equal("—", TransferFormatter.Format(double.NaN, TransferUnit.BytesPerSecond));
        Assert.Equal("—", TransferFormatter.Format(double.MaxValue, TransferUnit.BitsPerSecond));
    }

    [Fact]
    public void RateSmoothingRespondsToChangesWithoutReplacingTheFirstSample()
    {
        Assert.Equal(100, NetworkRateCalculator.Smooth(null, 100));
        Assert.Equal(165, NetworkRateCalculator.Smooth(100, 200));
    }

    [Fact]
    public void LatencyStatisticsRequireEnoughSamplesAndUseTwentySampleWindow()
    {
        Assert.Null(LatencyStatistics.Calculate([10]).JitterMs);
        Assert.Null(LatencyStatistics.Calculate([10, null, 14, 12]).PacketLossPercent);

        var result = LatencyStatistics.Calculate([10, null, 14, 12, 10]);
        Assert.Equal(10, result.LatencyMs);
        Assert.Equal(8d / 3, result.JitterMs!.Value, 6);
        Assert.Equal(20, result.PacketLossPercent);

        var rolling = Enumerable.Repeat<double?>(20, 20).Append(null).ToArray();
        Assert.Equal(5, LatencyStatistics.Calculate(rolling).PacketLossPercent);
    }

    [Fact]
    public void SettingsDefaultToTransparentOverlayAndOptInPing()
    {
        var settings = AppSettings.Defaults();
        Assert.False(settings.BackgroundEnabled);
        Assert.False(settings.PingEnabled);
        Assert.Equal("1.1.1.1", settings.PingTarget);
        Assert.Equal(OverlayPosition.TopCenter, settings.Position);
        Assert.Equal(13, settings.FontSize);
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("example.com", true)]
    [InlineData("", false)]
    [InlineData("not a host", false)]
    [InlineData("host/path", false)]
    public void PingTargetValidationAcceptsHostNamesAndAddressesOnly(string target, bool expected) =>
        Assert.Equal(expected, PingTargetValidation.IsValid(target));

    [Fact]
    public void SettingsValidationRestoresOutOfRangeAndInvalidValues()
    {
        var settings = new AppSettings
        {
            RefreshIntervalMs = 13,
            PingIntervalMs = 1,
            PingTimeoutMs = 0,
            FontSize = 90,
            OffsetX = -500,
            BackgroundColor = "nope",
            TransferUnit = (TransferUnit)9,
            Position = (OverlayPosition)99
        };

        settings.Validate();

        Assert.Equal(500, settings.RefreshIntervalMs);
        Assert.Equal(10000, settings.PingIntervalMs);
        Assert.Equal(2000, settings.PingTimeoutMs);
        Assert.Equal(24, settings.FontSize);
        Assert.Equal(-200, settings.OffsetX);
        Assert.Equal("#111720", settings.BackgroundColor);
        Assert.Equal(TransferUnit.BytesPerSecond, settings.TransferUnit);
        Assert.Equal(OverlayPosition.TopCenter, settings.Position);
    }

    [Fact]
    public void SettingsStorePersistsInterfaceAndOptionalMetrics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tacho-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = AppSettings.Defaults();
            settings.InterfaceId = "adapter-id";
            settings.ShowJitter = true;
            settings.ShowSessionData = true;
            settings.BackgroundEnabled = true;
            store.Save(settings);

            var loaded = store.Load();
            Assert.Equal("adapter-id", loaded.InterfaceId);
            Assert.True(loaded.ShowJitter);
            Assert.True(loaded.ShowSessionData);
            Assert.True(loaded.BackgroundEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void SettingsStorePreservesDefaultsForMissingFieldsAndRecoversMalformedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tacho-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"FontSize":16}""");
            var store = new SettingsStore(path);
            var partial = store.Load();
            Assert.Equal(16, partial.FontSize);
            Assert.False(partial.BackgroundEnabled);
            Assert.False(partial.PingEnabled);
            Assert.True(partial.ShowDownload);

            File.WriteAllText(path, "{malformed");
            var recovered = store.Load();
            Assert.Equal(13, recovered.FontSize);
            Assert.False(recovered.BackgroundEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Theory]
    [InlineData(OverlayPosition.TopLeft, 110, 60)]
    [InlineData(OverlayPosition.TopCenter, 400, 60)]
    [InlineData(OverlayPosition.TopRight, 690, 60)]
    [InlineData(OverlayPosition.BottomLeft, 110, 600)]
    [InlineData(OverlayPosition.BottomCenter, 400, 600)]
    [InlineData(OverlayPosition.BottomRight, 690, 600)]
    public void PositionUsesMetronAnchorsAndRenderedWindowSize(OverlayPosition position, double expectedX, double expectedY)
    {
        var area = new Rect(100, 50, 800, 600);
        var placement = PositionService.ComputePlacement(area, 200, 40, position, 0, 0);
        Assert.Equal(expectedX, placement.X);
        Assert.Equal(expectedY, placement.Y);
    }

    [Fact]
    public void PositionAppliesSignedOffsetsThenClampsToDisplay()
    {
        var area = new Rect(100, 50, 800, 600);
        var moved = PositionService.ComputePlacement(area, 200, 40, OverlayPosition.TopCenter, -20, 15);
        var clamped = PositionService.ComputePlacement(area, 200, 40, OverlayPosition.BottomRight, 500, 500);
        Assert.Equal((380d, 75d), moved);
        Assert.Equal((700, 610), clamped);
    }

    private sealed class IncrementingNetworkProvider(InterfaceOption adapter) : INetworkDataProvider
    {
        private int sampleCount;

        public IReadOnlyList<InterfaceOption> GetInterfaces() => [adapter];

        public NetworkCounters ReadCounters(string interfaceId)
        {
            var count = Interlocked.Increment(ref sampleCount);
            return new NetworkCounters((ulong)(count * 1000), (ulong)(count * 500));
        }
    }

    private sealed class FixedPingProvider(double? latencyMs) : IPingProvider
    {
        public string Target { get; private set; } = string.Empty;
        public int TimeoutMs { get; private set; }

        public Task<double?> ProbeAsync(string target, int timeoutMs, CancellationToken cancellationToken)
        {
            Target = target;
            TimeoutMs = timeoutMs;
            return Task.FromResult(latencyMs);
        }
    }
}
