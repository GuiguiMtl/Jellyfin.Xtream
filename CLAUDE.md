# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Jellyfin.Xtream is a Jellyfin server plugin that integrates content from an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/) (IPTV provider protocol) into Jellyfin as Live TV, Video On-Demand, Series, and TV Catch-up. This repo is a fork of `Kevinjil/Jellyfin.Xtream` (releases publish from `guiguiMtl/Jellyfin.Xtream`).

The output is a single `Jellyfin.Xtream.dll` loaded by a Jellyfin server. There is no standalone app to run — to test changes you build the DLL and drop it into a Jellyfin server's plugin directory, then restart the server.

## Build & Develop

```powershell
dotnet build Jellyfin.Xtream.sln                      # Debug build
dotnet build Jellyfin.Xtream.sln -c Release           # Release build
dotnet publish Jellyfin.Xtream -c Release -o ./publish # Produces the deployable DLL
```

- **Targets .NET 9.0**, builds against Jellyfin ABI `10.11.0.0` (`Jellyfin.Controller` / `Jellyfin.Model` 10.11.0). Bumping the Jellyfin version means updating the package versions, `build.yaml`, and `repository.json` together.
- **`TreatWarningsAsErrors` is on** with `AnalysisMode=AllEnabledByDefault` plus StyleCop, SerilogAnalyzer, and MultithreadingAnalyzer, governed by `../jellyfin.ruleset` and `.editorconfig`. Style/analyzer violations fail the build — match existing conventions (GPL license header on every `.cs` file, XML doc comments on public members, `ConfigureAwait(false)` on awaits).
- **No test project exists** despite the `test.yaml` workflow (which calls a shared Jellyfin meta-plugin workflow). Verification is build + manual testing against a live Jellyfin server.

### Version bump for a release

When releasing, keep these in sync: `AssemblyVersion`/`FileVersion` in `Jellyfin.Xtream.csproj`, `version` in `build.yaml`, and the `repository.json` entry. Releases on GitHub trigger `publish.yaml`, which builds, attaches checksummed artifacts, and regenerates the plugin repository manifest.

## Architecture

### Plugin lifecycle & the singleton
- `Plugin.cs` is the entry point (`BasePlugin<PluginConfiguration>`). It exposes a static `Plugin.Instance` singleton used pervasively throughout the codebase to reach `Configuration`, `Creds`, `StreamService`, and `TaskService`. New code follows this pattern rather than injecting the config.
- `PluginServiceRegistrator.cs` registers DI services: `IXtreamClient`, the `ILiveTvService`, the three `IChannel` implementations, and the VOD metadata provider.
- `Plugin.UpdateConfiguration` deliberately cancels + re-queues Jellyfin's guide/channel refresh tasks (via `TaskService`) so config changes immediately re-sync content and credential changes purge stale entries.

### Content surfaces (four delivery paths)
Content reaches Jellyfin through distinct integration points, all backed by `StreamService`:
- **`LiveTvService`** (`ILiveTvService`) — Live TV channels + EPG. EPG is memory-cached for 10 minutes. Live streams go through restreaming (see below).
- **`VodChannel`, `SeriesChannel`, `CatchupChannel`** (`IChannel`) — browsable channels for VOD, Series, and Catch-up, each gated by a visibility flag in config.
- **`XtreamVodProvider`** (`ICustomMetadataProvider<Movie>`) — enriches VOD movie items with Xtream metadata.

### `StreamService` — the core (`Service/StreamService.cs`)
The hub for all stream logic. Key responsibilities:
- **GUID encoding scheme**: Jellyfin identifies items by GUID, but Xtream uses integer IDs. `ToGuid(i0, i1, i2, i3)` packs four 32-bit ints into a GUID and `FromGuid` unpacks them. The first int is always a **prefix constant** (`LiveTvPrefix`, `VodCategoryPrefix`, `SeriesPrefix`, `CatchupPrefix`, etc., all `0x5d774c3x`) that tags what kind of item the GUID represents. When adding a new item type, add a new prefix constant and route on it. This is fundamental — most `channelId` parsing starts by unpacking the GUID and checking the prefix.
- **Config-driven filtering**: `IsConfigured` and the `Get*` methods filter Xtream content against the per-category/per-item selections stored in `PluginConfiguration`'s `SerializableDictionary<int, HashSet<int>>` fields (empty HashSet = whole category enabled).
- **`GetMediaSourceInfo`** builds the `MediaSourceInfo` (URLs embed `username/password`; catch-up uses the `timeshift.php` endpoint).
- **`ParseName`** strips `[TAG]` / `|TAG|` style tags (incl. Unicode pipe + block-element variants) from stream names into a `ParsedName`.
- **`ProbeStreamAsync`** runs `ffprobe` (prefers Jellyfin's bundled `/usr/lib/jellyfin-ffmpeg/ffprobe`, falls back to PATH) to detect codecs for live TV. This is plugin-side probing that bypasses Jellyfin's probe cache — for live channels, `MediaStreams` is populated from this and `SupportsProbing` is disabled to avoid a codec-mismatch crash.

### Live TV restreaming (`Service/Restream.cs`, `WrappedBufferStream`)
Live channels are proxied, not passed through directly. `Restream` (an `ILiveStream`/`IDirectStreamProvider`) pulls the upstream HTTP stream into a ring buffer (`WrappedBufferStream`, size = `BufferSizeMiB`, default 64) and re-serves it via Jellyfin's `/LiveTv/LiveStreamFiles/` path. It handles HTTPS→HTTP redirect downgrades manually and shares one upstream connection across consumers (`EnableStreamSharing`, `ConsumerCount`).

### Xtream API client (`Client/`)
- `XtreamClient` (`IXtreamClient`) wraps all `player_api.php` calls. It uses **Newtonsoft.Json** (not System.Text.Json) with a custom error handler (`NullableEventHandler`) that swallows deserialization errors for nullable properties — Xtream servers return wildly inconsistent JSON. Several custom `JsonConverter`s in `Client/` (`StringBoolConverter`, `SingularToListConverter`, `OnlyObjectConverter`, `Base64Converter`) absorb these inconsistencies. When a field parses unreliably, the convention is to make it nullable and/or add a converter, not to throw.
- `Client/Models/` holds the API response DTOs.
- `UpdateUserAgent()` is called on construction and on every config update.

### Configuration UI (`Configuration/Web/` + `Api/`)
- Admin UI is a set of embedded HTML/JS/CSS resources (`Configuration/Web/`, embedded via the `.csproj` `EmbeddedResource` glob and listed in `Plugin.GetPages()`).
- The JS pages call `XtreamController` (`Api/XtreamController.cs`, routed at `/Xtream`) to list categories/items live from the Xtream server so the admin can pick what to expose. `Api/Models/` holds the response DTOs for this internal API.

## Conventions & gotchas

- **Credentials leak through paths**: stream URLs embed the Xtream username/password and Jellyfin publishes remote paths. This is a known, documented limitation — don't treat exposed paths as a new bug.
- **`DataVersion`** (`Plugin.DataVersion`, used by the channels) combines the assembly version with the config hash so Jellyfin invalidates cached channel content on plugin update *or* config change.
- Prefer `Plugin.Instance.Configuration` / `Plugin.Instance.Creds` for access to settings, consistent with the rest of the code.
