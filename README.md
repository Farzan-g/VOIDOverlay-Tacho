# VOID Overlay Tacho

VOID Overlay Tacho is a lightweight Windows network overlay and an independent member of the VOID Overlay family. It displays actual per-interface traffic counters and optional ICMP latency measurements. Its throughput values represent current observed traffic, **not** maximum internet/ISP capacity.

## Features

- Transparent, compact, click-through, always-on-top overlay with a disabled background by default.
- Download and upload rates from Windows network-interface IP statistics, measured from byte-counter deltas using monotonic elapsed time.
- Automatic or manual interface selection; interfaces are not summed.
- Optional configurable ICMP ping, rolling jitter, and ping-loss readings.
- Optional in-memory session download/upload totals.
- Injected `INetworkDataProvider` and `IPingProvider` boundaries with Windows API defaults and deterministic test providers.
- Six-anchor, multi-monitor positioning following Metron's positioning model.
- Per-user JSON preferences, tray menu, current-user startup option, and local diagnostic logs.
- The provided Tacho logo is used as the embedded application, overlay, settings, About, and tray icon.
- No telemetry, traffic interception, packet-content inspection, automatic bandwidth test, or administrator requirement.

## Requirements and installation

Tacho targets WPF on `net10.0-windows` and publishes framework-dependent Windows x64 output. Install the .NET 10 Desktop Runtime (x64) if it is not already present. Copy the `artifacts\publish\win-x64` output to a writable user location and run `VOIDOverlay.Tacho.exe`.

The application does not install drivers, modify firewall settings, or require elevation. Overlay behavior may be limited by exclusive fullscreen, protected content, game settings, or anti-cheat policy.

## First launch and tray

The overlay starts visible by default. Use the tray menu to open Settings, show/hide the overlay, lock its position, enable current-user startup, open About, or exit. Closing Settings hides it; **Exit** stops the app. A second launch requests that the existing instance reveal its overlay.

Default visible fields are download, upload, and ping. ICMP measurement itself is disabled by default, so ping remains unavailable (`—`) until enabled. Download/upload values show `—` until a valid counter delta has been observed.

## Interface selection and measurements

Automatic mode skips common virtual-adapter name/description markers whenever an active physical-looking adapter is available, prefers candidates reporting a default gateway, then picks the highest reported link speed. If no candidate has a gateway, it picks the highest-speed active eligible adapter; if only virtual adapters are active, they remain eligible. Ties are resolved by interface ID. Because Windows does not expose a universal physical/virtual flag, vendor-specific virtual adapters may still qualify. No unrelated interfaces are silently aggregated. A manual adapter can be selected in Settings.

The counter sampling interval defaults to 500 ms. Speed uses decimal rate prefixes (1 KB/s = 1,000 B/s; bit mode is derived by multiplying bytes by 8). A light exponential smoothing step weights the latest rate sample at 65% and the prior displayed rate at 35%, balancing flicker reduction with quick response to genuine changes. Windows counters can include protocol/control traffic and may differ from application payload totals. A counter reset, adapter change, or invalid elapsed interval makes the affected sample unavailable rather than negative.

The app reports selected network-interface link state, not universal internet connectivity: an active adapter does not prove internet reachability. When the configured ICMP probe succeeds, that target was reachable at that time; a failed probe may mean the destination/network blocks ICMP, not that all internet access is down.

### Ping, jitter, and ping loss

Ping is optional and disabled by default. When enabled, Tacho sends ICMP Echo Requests to `1.1.1.1` by default every 10 seconds with a 2-second timeout. Settings allow a host name or IP target, 5/10/30/60-second cadence, and 1/2/5-second timeout. Host names can trigger the operating system's normal DNS lookup. Ping measures latency to that target, not universal internet latency.

Jitter is the mean absolute difference between consecutive successful round-trip measurements in the latest 20 probes; it is unavailable before two successful probes. Ping loss is the failed proportion of those latest probes, shown after at least five probes. This is probe loss only, not total traffic or application packet loss. A blocked ping is shown as unavailable; Tacho does not substitute a TCP or HTTP estimate.

### Session data

Optional session counters start at zero when monitoring a selected adapter begins and accumulate observed counter deltas in memory, formatted in binary units (KiB = 1,024 bytes). They reset when changing to another adapter and do not include traffic while the app is stopped or disconnected. These are not persistent usage totals and are not authoritative ISP billing data.

## Settings and privacy

Settings are saved under `%LOCALAPPDATA%\VOIDOverlay\Tacho\settings.json`; logs are under `%LOCALAPPDATA%\VOIDOverlay\Tacho\logs`. Invalid settings are validated to safe defaults; malformed JSON is logged and defaults are loaded. Appearance, visibility, units, adapter, sampling, ping, and position choices persist per user.

No outbound requests are made for telemetry, bandwidth testing, or general connectivity monitoring. If ICMP is enabled, the only intentional application-level network probes are the configured pings at the selected cadence (default target `1.1.1.1`); name-based targets may also cause standard DNS resolution. Tacho does not inspect packet contents or persist traffic histories.

## Build, test, and publish

Requirements: Windows and the .NET 10 SDK. From the Tacho directory:

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\publish.ps1 -Configuration Release
```

Build/test outputs use `artifacts\build`; framework-dependent x64 publish output is staged and validated at `artifacts\publish\win-x64`. Publish scripts do not create archives. The original `assets\Tacho.png` is retained unchanged; `assets\Tacho.ico` contains 16, 24, 32, 48, 64, 128, and 256-pixel variants and is embedded for the executable and WPF windows/tray. The project has no third-party application runtime package references; tests use xUnit, Microsoft.NET.Test.Sdk, and coverlet.

## Project structure

- `src\VOIDOverlay.Tacho` — WPF application, settings, network services, tray, and positioning.
- `tests\VOIDOverlay.Tacho.Tests` — deterministic tests for counters, interface policy, formatting, settings, statistics, and positioning.
- `scripts` — independent build, test, and publish workflows.
- `docs\compatibility.md` — product-family audit and Tacho-specific policies.

## Troubleshooting and known limitations

- **Rates show `—`:** wait for the first sample, confirm an adapter is active, or choose an interface manually.
- **Unexpected adapter:** select the desired network interface in Settings; virtual adapters exposed as Ethernet can be selected automatically.
- **Ping shows `—`:** enable ping and confirm the configured target responds to ICMP. Firewalls and some networks block Echo Requests.
- **Overlay is not visible in a game:** use borderless-windowed mode; Tacho does not circumvent fullscreen or anti-cheat restrictions.
- **Settings do not persist:** check write access to the Local AppData folder and inspect the per-day diagnostic log.
- Counter semantics depend on Windows adapter/driver statistics. Virtualization, counter resets, and adapter downtime can affect reported rates and session totals.

The automated suite does not verify tray rendering, real NIC hardware counters, ping reachability, game compatibility, or multi-monitor runtime behavior. No screenshots or release URLs are included because none have been captured or supplied.

## License

Tacho is licensed under the MIT License; see [LICENSE](./LICENSE). Test dependencies retain their upstream licenses.
