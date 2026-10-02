# BundleCacheBoost

Client mod for **SPT 4.0.x** that makes the "Loading bundles" step at game start near instant when you play with modded content on a remote server or as a **Fika client**.

## The problem

When the client connects to a non-local server, SPT 4.0 checks every cached mod bundle in `SPT/user/cache/bundles` on **every launch**: one at a time, reading each file fully into memory and computing its CRC32. With large weapon/gear packs that is thousands of files and tens of GB read from disk before you reach the main menu, even when nothing changed.

## What this mod does

- **Verification cache:** stores size, last write time and CRC of each verified bundle in `SPT/user/cache/BundleCacheBoost.json`. If a bundle's file is unchanged and the server still reports the same CRC, it is not read again.
- **Parallel hashing:** bundles that do need checking (first launch, mod updates) are hashed on several threads, streamed from disk instead of loaded whole.
- **Same outcome as SPT:** missing bundles or bundles whose CRC differs from the server are re-downloaded exactly as before.

The first launch after installing builds the cache and takes about as long as usual. Later launches only check file sizes and dates.

The log shows a summary line, e.g. `Verified 3135 bundles in 900 ms: 3135 from cache, 0 hashed, 0 CRC mismatch, 0 missing.`

## When it helps

- You connect to a remote SPT server, or play as a Fika client, **with mods that add bundles**.
- It does nothing in local single-player: there SPT loads bundles straight from the mod folders and does not run this check.
- SPT 4.1+ ships its own bundle CRC cache. The mod detects it and stays inactive there.

## Install

Extract the zip into your SPT folder (the one containing `EscapeFromTarkov.exe`). You should end up with
`BepInEx/plugins/BundleCacheBoost/BundleCacheBoost.dll`.

Client only: the server, host and other players do not need it.

To uninstall, delete `BepInEx/plugins/BundleCacheBoost`. You can also delete `SPT/user/cache/BundleCacheBoost.json`.

## Configuration (F12 / `BepInEx/config/com.mexes.bundlecacheboost.cfg`)

| Option | Default | Description |
|---|---|---|
| Enabled | true | Turn the mod off without uninstalling it. |
| ForceFullVerify | false | Re-hash every bundle on the next launch, then turns itself off. Use it if you suspect a corrupted bundle. |
| MaxThreads | half your CPU threads (2-8) | Threads used to hash bundles that are not cached. |

Changes take effect on the next launch.

## Trade-off

A bundle counts as valid while its size and last write time match the cache and the server CRC is unchanged. A file corrupted on disk without its timestamp changing (very rare) would not be detected until you use **ForceFullVerify**.

## Building

Requires the .NET SDK and an SPT 4.0.x install for references:

```
dotnet build -c Release -p:GameDir="C:\path\to\SPT"
```

The Release build writes `dist/BundleCacheBoost-<version>.zip`.

## Changelog

### 1.0.0
- First release.

## License

MIT
