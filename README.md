# DJTrans — Media Transfer for DJI Cameras (Windows)

**English** | [简体中文](README_CN.md)

A small, fast, reliable Windows desktop app for offloading media from DJI Osmo Action / Pocket cameras (they mount as USB mass-storage drives). Auto-detects the device on plug-in, shows a thumbnail grid, fast multi-select / invert-select, and exports with **resumable transfers, block-hash verification, and incremental skip of already-exported files** — plus upload, delete and safe-eject.

> DJI ships no Windows desktop tool for media offloading (Mimo is mobile-only). Manual copy-paste has no increment, no verification, and restarts from zero after a hiccup. DJTrans fixes that.

## ✨ Features

| | |
|---|---|
| 🔌 **Auto-detect** | Device recognized whether plugged in before or after launch (volume hot-plug events + polling fallback); DJI volumes auto-tagged |
| 🖼️ **Thumbnail grid** | Photo/video thumbnails via Windows Shell decoders (DNG uses embedded preview extraction); disk + memory cache; virtualized rendering for huge cards |
| 🖱️ **Fast selection** | Click / Ctrl / Shift / rubber-band / Ctrl+A select-all / **invert selection** / type filter / sort / zoom; `.lrf` proxy and `.srt` subtitle files excluded by default |
| 👁️ **Preview** | Double-click / Enter opens with the system default app (photos → image viewer, videos → default player) |
| ⬇️ **Export engine** | See below |
| ⬆️ **Upload** | PC → camera (into `DCIM\DJTRANS_IN`, same verification) |
| 🗑️ **Delete** | Strong confirmation (removable drives have no Recycle Bin — deletion is permanent) |
| ⏏ **Safe eject** | FSCTL volume lock + dismount |

Export engine (camera → PC):

- **Resumable transfers**: 8MB-block XxHash3 checkpoints + atomic journal; resumes **exactly at the kill point** after a hard process kill or unplugged cable (verified on real hardware: a 2.4GB file resumed at 2256MB, re-copying only the tail)
- **Verification**: full block-hash comparison after transfer — a corrupt file is never delivered (51 files verified SHA256-identical on real hardware)
- **Incremental skip**: destination files with identical content (size + head/tail fingerprint) are skipped instantly — re-export costs seconds, not minutes
- **Conflict policies**: smart-skip / ask / overwrite / skip / keep-both ("apply to all" supported)
- **Queue**: parallel workers (measured 65–70 MB/s over camera USB), per-file + overall progress, speed, ETA, pause/resume/cancel/retry
- **Details**: destination free-space precheck, folder layout (mirror camera structure / by shooting date / flat), timestamps preserved, single-instance mutex

## 📥 Download

Grab a zip from the [**Releases**](../../releases) page:

| Package | Size | For |
|---|---|---|
| `DJTrans-vX.Y.Z-win-x64-self-contained.zip` | ~50MB | **Recommended** — zero dependencies, unzip and run |
| `DJTrans-vX.Y.Z-win-x64-framework-dependent.zip` | ~1MB | Machines that already have the .NET 10 Desktop Runtime |

Requires Windows 10 1809+ / Windows 11, x64.

## 🔧 Build from source

```bash
git clone <repo>
cd DJTrans
dotnet build DJTrans.slnx -c Release     # build
dotnet test tests/DJTrans.Core.Tests     # 55 unit tests
dotnet run --project src/DJTrans -c Release   # run
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Publish a single file:

```bash
dotnet publish src/DJTrans -c Release -o out -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true
```

Pushing a `v*` tag triggers CI: tests run, both zips are packaged, and a GitHub Release is created automatically.

## 🏗️ Architecture

C# / .NET 10 / WinForms (PerMonitorV2 HiDPI), zero third-party runtime dependencies (only System.IO.Hashing).

```
src/DJTrans/        UI: owner-drawn virtual thumbnail grid / transfer queue / dialogs
src/DJTrans.Core/   UI-free testable library: media scan, volume hot-plug watcher,
                    resumable transfer engine, DNG embedded-preview extraction
tests/              xunit: engine / resume / conflicts / verification / state machine / scan
docs/engineering/   Engineering log: requirements, acceptance criteria, hardware evidence (PLAN.md)
```

## ⚠️ Known limitations

- USB mass-storage devices only (**all DJI Action/Pocket models work this way**); MTP devices (phones etc.) are not supported yet
- HEVC (H.265) video thumbnails depend on system codecs; a placeholder icon is shown if absent
- Deleting from removable drives is permanent (Windows provides no Recycle Bin for them)
- Interrupted uploads restart from scratch (journaled resume currently covers the download direction only)

No telemetry, no networking — your data moves only between directories you choose. License: [MIT](LICENSE).
