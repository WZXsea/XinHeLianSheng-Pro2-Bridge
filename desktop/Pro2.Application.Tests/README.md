# Shared application / macOS native IPC acceptance

This console suite launches the real `pro2-native-host` through `PlatformBridge` and `ControllerService`. It verifies the application/native boundary without generating controller input or starting Bluetooth discovery.

```bash
swift build --package-path macos --product pro2-native-host
PRO2_NATIVE_HOST="/absolute/path/pro2-native-host" dotnet run --project desktop/Pro2.Application.Tests/Pro2.Application.Tests.csproj
```

An explicit host path may also be passed as the first program argument after `--`.

The suite covers:

1. Shared service initialization, backend inspection, native state-adapter self-test, four idle sessions, stop and EOF release.
2. Thirty-two concurrent first requests and one helper startup, with request IDs matched to the correct response.
3. String-valued unsolicited IDs, event subscriber failures and continued numeric request processing.
4. All six UI output modes: supported exact identities require their real backend, while Pro 2 and professional IMU identities report their explicit implementation boundary.
5. Reusable stop and the final shutdown response arriving before process exit completes outstanding requests.
6. Eight real helper restart cycles, per-session request ownership, process-session removal and pipe descriptor release.
7. Three isolated service policy cases use a scripted IPC peer: pending slot ownership and disabled slots, neutral rumble/state reset, and helper-exit session cleanup. These cases exercise application policy without creating physical controller input.
8. C# to native gyro settings round-trip, original 0.1..4.0 normalization, and native target-axis scaling after the coordinate/unit adapter.

The service test disables automatic discovery in memory. It preserves the user's saved settings. A machine with a working licensed broker may briefly create supported virtual output identities; the suite destroys each identity before proceeding.

Hardware discovery, physical Bluetooth input, virtual output as observed by a game and physical rumble require their separate device acceptance runs.

## Driver cache file acceptance

The driver installer suite uses the existing manifest-verified official disk image and local HTTP handlers; it performs no network download or installation. It covers complete SHA256/size validation, valid cache reuse, malformed bodies/headers, the 16 MiB cap, unknown-length streams, atomic publication, twelve concurrent instances, three independent processes and cancellation cleanup.

```bash
dotnet run --project desktop/Pro2.Application.Tests/Pro2.Application.Tests.csproj -- --installer-only /tmp/pro2-libvirtualhid-macOS-universal-verified.dmg
```

The fixture remains outside the repository. Tests create and remove their own temporary cache directories. The embedded application resource contains source/size/hash metadata only.
