# Cyberstat

Cyberstat is a local Cities: Skylines II mod that exports city telemetry to an HTTPS processor while the simulation runs. Once per in-game hour, it posts JSON snapshots for economy, resources, production, citizens, buildings, and roads.

## Requirements

- .NET SDK
- Cities: Skylines II game assemblies
- `just`
- Python 3 and OpenSSL, for the optional local dummy sink

The project targets `net48` and expects `CSII_MANAGEDPATH` to point at the game's `Cities2_Data/Managed` directory. The included Nix flake provides a development shell with the expected tools and default Linux Steam paths:

```sh
nix develop
```

## Build

```sh
just build
```

To build and copy the mod into the local Cities: Skylines II mods cache:

```sh
just build_and_place
```

This uses `CSII_LOCALMODSPATH` and copies `Cyberstat.dll`, `Cyberstat.pdb`, and `mod.json` into `Cyberstat/`.

## Telemetry Processor

Cyberstat posts telemetry to:

```text
https://<CYBERSTAT_PROCESSOR_ADDRESS>:<CYBERSTAT_PROCESSOR_PORT>/
```

If the environment variables are not set, it defaults to:

```text
https://localhost:2145/
```

Payloads are sent as JSON envelopes containing the city name, simulation date, absolute day, and payload data. The request query includes `program=cyberstat` and `payload_type=<snapshot>`.

## Local Dummy Sink

For local testing, run:

```sh
just dummy_sink
```

The dummy sink starts an HTTPS server on `127.0.0.1:2145`, generates a self-signed localhost certificate if needed, and writes received payload records to `sink-payloads/`.

## Repository Layout

- `Cyberstat.cs` - mod entry point, simulation system, snapshot generation, and HTTP publisher
- `Cyberstat.csproj` - C# project file and Cities: Skylines II assembly references
- `mod.json` - local mod metadata
- `justfile` - build, install, and dummy sink commands
- `scripts/dummy_sink.py` - local HTTPS receiver for telemetry payloads
- `bak/` - sample captured payloads
