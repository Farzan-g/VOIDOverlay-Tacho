using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VOIDOverlay.Tacho;

public sealed record InterfaceOption(
    string Id, string Name, NetworkInterfaceType Type, OperationalStatus Status, long LinkSpeed,
    bool HasDefaultGateway, bool IsLikelyVirtual);

public sealed record NetworkSnapshot(
    string InterfaceName,
    bool IsConnected,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    ulong SessionDownloadedBytes,
    ulong SessionUploadedBytes);

public sealed record LatencySnapshot(double? LatencyMs, double? JitterMs, double? PacketLossPercent);

public sealed record NetworkCounters(ulong BytesReceived, ulong BytesSent);

public interface INetworkDataProvider
{
    IReadOnlyList<InterfaceOption> GetInterfaces();
    NetworkCounters? ReadCounters(string interfaceId);
}

public interface IPingProvider
{
    Task<double?> ProbeAsync(string target, int timeoutMs, CancellationToken cancellationToken);
}

public sealed class WindowsNetworkDataProvider : INetworkDataProvider
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vmware", "virtualbox", "hyper-v", "hyperv", "tap", "tun", "vpn",
        "docker", "wsl", "veth", "bridge", "host-only"
    ];
    private static int gatewayInformationWarningLogged;

    public IReadOnlyList<InterfaceOption> GetInterfaces()
    {
        var options = new List<InterfaceOption>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase))
        {
            var hasDefaultGateway = false;
            try
            {
                hasDefaultGateway = adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                    !gateway.Address.Equals(System.Net.IPAddress.Any) &&
                    !gateway.Address.Equals(System.Net.IPAddress.IPv6Any));
            }
            catch (NetworkInformationException exception)
            {
                if (Interlocked.Exchange(ref gatewayInformationWarningLogged, 1) == 0)
                    AppLog.Warning($"Routing information for a network adapter could not be read. {exception.Message}");
            }

            options.Add(new InterfaceOption(
                adapter.Id, adapter.Name, adapter.NetworkInterfaceType, adapter.OperationalStatus, adapter.Speed,
                hasDefaultGateway, LooksVirtual(adapter.Name, adapter.Description)));
        }
        return options;
    }

    private static bool LooksVirtual(string name, string description)
    {
        var identity = $"{name} {description}";
        return VirtualAdapterMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public NetworkCounters? ReadCounters(string interfaceId)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(item => item.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
        {
            return null;
        }

        var statistics = adapter.GetIPStatistics();
        return new NetworkCounters(
            (ulong)Math.Max(0, statistics.BytesReceived),
            (ulong)Math.Max(0, statistics.BytesSent));
    }
}

public static class NetworkInterfaceSelector
{
    public static InterfaceOption? SelectAutomatic(IEnumerable<InterfaceOption> interfaces)
    {
        var active = interfaces
            .Where(adapter => adapter.Status == OperationalStatus.Up &&
                adapter.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown))
            .ToArray();
        var physicalCandidates = active.Where(adapter => !adapter.IsLikelyVirtual).ToArray();
        if (physicalCandidates.Length > 0)
        {
            active = physicalCandidates;
        }
        var routed = active.Where(adapter => adapter.HasDefaultGateway).ToArray();
        return (routed.Length > 0 ? routed : active)
            .OrderByDescending(adapter => adapter.LinkSpeed)
            .ThenBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static InterfaceOption? Select(IEnumerable<InterfaceOption> interfaces, string interfaceId) =>
        string.IsNullOrEmpty(interfaceId)
            ? SelectAutomatic(interfaces)
            : interfaces.FirstOrDefault(adapter => adapter.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
}

public static class NetworkRateCalculator
{
    public static double? BytesPerSecond(ulong previousBytes, ulong currentBytes, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || currentBytes < previousBytes)
        {
            return null;
        }

        return (currentBytes - previousBytes) / elapsedSeconds;
    }

    public static double Smooth(double? previousValue, double currentValue)
    {
        const double currentWeight = 0.65;
        return previousValue is double previous && double.IsFinite(previous) && previous >= 0
            ? previous * (1 - currentWeight) + currentValue * currentWeight
            : currentValue;
    }
}

public static class TransferFormatter
{
    public static string Format(double? bytesPerSecond, TransferUnit unit)
    {
        if (bytesPerSecond is not double bytes || !double.IsFinite(bytes) || bytes < 0)
        {
            return "—";
        }

        var value = unit == TransferUnit.BitsPerSecond ? bytes * 8 : bytes;
        var suffix = unit == TransferUnit.BitsPerSecond ? "bps" : "B/s";
        if (!double.IsFinite(value))
        {
            return "—";
        }

        var prefixes = new[] { "K", "M", "G" };
        var scale = 1000d;
        var index = -1;
        while (value >= scale && index < prefixes.Length - 1)
        {
            value /= scale;
            index++;
        }

        return index < 0
            ? $"{value:F0} {suffix}"
            : $"{value:F1} {prefixes[index]}{suffix}";
    }
}

public sealed class NetworkMonitorService : IDisposable
{
    private readonly AppSettings settings;
    private readonly INetworkDataProvider provider;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private string previousInterfaceId = string.Empty;
    private string sessionInterfaceId = string.Empty;
    private ulong previousReceived;
    private ulong previousSent;
    private long previousTimestamp;
    private double? smoothedDownload;
    private double? smoothedUpload;
    private ulong sessionReceived;
    private ulong sessionSent;
    private bool interfaceFailureLogged;
    private bool counterFailureLogged;

    public NetworkMonitorService(AppSettings settings, INetworkDataProvider? provider = null)
    {
        this.settings = settings;
        this.provider = provider ?? new WindowsNetworkDataProvider();
        token = cancellation.Token;
        worker = Task.Run(() => RunAsync(this.settings));
    }

    public event Action<NetworkSnapshot>? SnapshotAvailable;

    public void RefreshSettings()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending sample already observes the latest settings.
        }
    }

    private async Task RunAsync(AppSettings currentSettings)
    {
        while (!token.IsCancellationRequested)
        {
            PublishSample(currentSettings);
            try
            {
                await wake.WaitAsync(currentSettings.RefreshIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void PublishSample(AppSettings currentSettings)
    {
        InterfaceOption? selected;
        try
        {
            selected = NetworkInterfaceSelector.Select(
                provider.GetInterfaces(), currentSettings.InterfaceId);
        }
        catch (NetworkInformationException exception)
        {
            if (!interfaceFailureLogged)
                AppLog.Warning($"Network interfaces could not be enumerated. {exception.Message}");
            interfaceFailureLogged = true;
            SnapshotAvailable?.Invoke(new NetworkSnapshot("Unavailable", false, null, null, sessionReceived, sessionSent));
            return;
        }
        interfaceFailureLogged = false;

        if (selected is null || selected.Status != OperationalStatus.Up)
        {
            ResetBaseline();
            SnapshotAvailable?.Invoke(new NetworkSnapshot(selected?.Name ?? "No active interface", false, null, null, sessionReceived, sessionSent));
            return;
        }

        try
        {
            var counters = provider.ReadCounters(selected.Id);
            if (counters is null)
            {
                ResetBaseline();
                SnapshotAvailable?.Invoke(new NetworkSnapshot(selected.Name, false, null, null, sessionReceived, sessionSent));
                return;
            }

            counterFailureLogged = false;
            var received = counters.BytesReceived;
            var sent = counters.BytesSent;
            var now = Stopwatch.GetTimestamp();
            double? download = null;
            double? upload = null;

            if (previousInterfaceId == selected.Id && previousTimestamp != 0)
            {
                var elapsed = (now - previousTimestamp) / (double)Stopwatch.Frequency;
                var rawDownload = NetworkRateCalculator.BytesPerSecond(previousReceived, received, elapsed);
                var rawUpload = NetworkRateCalculator.BytesPerSecond(previousSent, sent, elapsed);
                download = rawDownload is double currentDownload
                    ? smoothedDownload = NetworkRateCalculator.Smooth(smoothedDownload, currentDownload)
                    : smoothedDownload = null;
                upload = rawUpload is double currentUpload
                    ? smoothedUpload = NetworkRateCalculator.Smooth(smoothedUpload, currentUpload)
                    : smoothedUpload = null;
                if (received >= previousReceived) sessionReceived += received - previousReceived;
                if (sent >= previousSent) sessionSent += sent - previousSent;
            }
            else if (!sessionInterfaceId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                sessionReceived = 0;
                sessionSent = 0;
                sessionInterfaceId = selected.Id;
                smoothedDownload = null;
                smoothedUpload = null;
            }

            previousInterfaceId = selected.Id;
            previousReceived = received;
            previousSent = sent;
            previousTimestamp = now;
            SnapshotAvailable?.Invoke(new NetworkSnapshot(selected.Name, true, download, upload, sessionReceived, sessionSent));
        }
        catch (NetworkInformationException exception)
        {
            ResetBaseline();
            if (!counterFailureLogged)
                AppLog.Warning($"Network counters for the selected adapter could not be read. {exception.Message}");
            counterFailureLogged = true;
            SnapshotAvailable?.Invoke(new NetworkSnapshot(selected.Name, false, null, null, sessionReceived, sessionSent));
        }
    }

    private void ResetBaseline()
    {
        previousInterfaceId = string.Empty;
        previousTimestamp = 0;
        previousReceived = 0;
        previousSent = 0;
        smoothedDownload = null;
        smoothedUpload = null;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        worker.GetAwaiter().GetResult();
        wake.Dispose();
        cancellation.Dispose();
    }
}

public static class LatencyStatistics
{
    public const int WindowSize = 20;

    public static LatencySnapshot Calculate(IReadOnlyList<double?> samples)
    {
        var window = samples.TakeLast(WindowSize).ToArray();
        var successes = window.Where(sample => sample.HasValue).Select(sample => sample!.Value).ToArray();
        var latency = window.Length > 0 ? window[^1] : null;
        double? jitter = successes.Length >= 2
            ? successes.Zip(successes.Skip(1), (left, right) => Math.Abs(right - left)).Average()
            : null;
        double? loss = window.Length >= 5
            ? (window.Length - successes.Length) * 100d / window.Length
            : null;
        return new LatencySnapshot(latency, jitter, loss);
    }
}

public sealed class PingMonitorService : IDisposable
{
    private readonly AppSettings settings;
    private readonly IPingProvider provider;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly List<double?> samples = [];
    private readonly Task worker;
    private bool pingFailureLogged;
    private string previousTarget = string.Empty;

    public PingMonitorService(AppSettings settings, IPingProvider? provider = null)
    {
        this.settings = settings;
        this.provider = provider ?? new WindowsPingProvider();
        token = cancellation.Token;
        worker = Task.Run(() => RunAsync(this.settings));
    }
    public event Action<LatencySnapshot>? SnapshotAvailable;

    private async Task RunAsync(AppSettings currentSettings)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(currentSettings.PingIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            if (!currentSettings.PingEnabled)
            {
                samples.Clear();
                previousTarget = string.Empty;
                pingFailureLogged = false;
                SnapshotAvailable?.Invoke(new LatencySnapshot(null, null, null));
                continue;
            }

            if (!currentSettings.PingTarget.Equals(previousTarget, StringComparison.OrdinalIgnoreCase))
            {
                samples.Clear();
                previousTarget = currentSettings.PingTarget;
            }

            double? sample = null;
            try
            {
                sample = await provider.ProbeAsync(currentSettings.PingTarget, currentSettings.PingTimeoutMs, token)
                    .ConfigureAwait(false);
                pingFailureLogged = false;
            }
            catch (PingException exception) when (exception.InnerException is SocketException)
            {
                LogPingFailure(exception);
            }
            catch (PingException exception)
            {
                LogPingFailure(exception);
            }
            catch (SocketException exception)
            {
                LogPingFailure(exception);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            samples.Add(sample);
            if (samples.Count > LatencyStatistics.WindowSize)
            {
                samples.RemoveAt(0);
            }

            SnapshotAvailable?.Invoke(LatencyStatistics.Calculate(samples));
        }
    }

    private void LogPingFailure(Exception exception)
    {
        if (pingFailureLogged) return;
        AppLog.Warning($"The configured ping probe failed ({exception.GetType().Name}).");
        pingFailureLogged = true;
    }

    public void RefreshSettings()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    public void Dispose()
    {
        cancellation.Cancel();
        worker.GetAwaiter().GetResult();
        wake.Dispose();
        cancellation.Dispose();
    }
}

public sealed class WindowsPingProvider : IPingProvider
{
    public async Task<double?> ProbeAsync(string target, int timeoutMs, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(target, timeoutMs).WaitAsync(cancellationToken).ConfigureAwait(false);
        return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
    }
}
