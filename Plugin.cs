using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using SPT.Custom.Models;
using SPT.Custom.Utils;

namespace FasterLoadingBundles
{
    [BepInPlugin(Guid, "Mexes-FasterLoadingBundles", Version)]
    [BepInDependency("com.SPT.custom")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.mexes.fasterloadingbundles";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ForceFullVerify;
        internal static ConfigEntry<int> MaxThreads;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Use the verification cache (size + last write time + CRC) so cached bundles are not re-read on every launch. Takes effect on next launch.");
            ForceFullVerify = Config.Bind("General", "ForceFullVerify", false,
                "Ignore the cache on the next launch and re-hash every bundle. Turns itself off afterwards.");
            MaxThreads = Config.Bind("General", "MaxThreads", Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2)),
                new ConfigDescription("Threads used to hash bundles that are not in the cache (first launch, mod updates). Takes effect on next launch.",
                    new AcceptableValueRange<int>(1, 32)));

            if (!Enabled.Value)
            {
                Log.LogInfo("Disabled in config.");
                return;
            }

            try
            {
                var target = FindTarget(out var reason);
                if (target == null)
                {
                    Log.LogWarning($"{reason} FasterLoadingBundles will stay inactive; SPT's default bundle check is used.");
                    return;
                }

                var ignoreCache = ForceFullVerify.Value;
                if (ignoreCache)
                {
                    ForceFullVerify.Value = false;
                }

                BundleVerifier.Init(Path.Combine(Paths.GameRootPath, "SPT/user/cache/FasterLoadingBundles.json"), ignoreCache, MaxThreads.Value);

                new Harmony(Guid).Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(ShouldAcquirePrefix)));
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize, SPT's default bundle check is used: {ex}");
            }
        }

        /// <summary>
        /// Only patch SPT 4.0.x: from 4.1 SPT ships its own BundleCrcCache, and any signature change
        /// means our replacement may no longer match what SPT expects.
        /// </summary>
        private static MethodInfo FindTarget(out string reason)
        {
            var sptCustom = typeof(BundleManager).Assembly;

            if (sptCustom.GetType("SPT.Custom.Utils.BundleCrcCache") != null)
            {
                reason = "This SPT version already caches bundle CRCs (BundleCrcCache), this mod is not needed.";
                return null;
            }

            var version = sptCustom.GetName().Version;
            if (version.Major != 4 || version.Minor != 0)
            {
                reason = $"Untested SPT version {version} (built for 4.0.x).";
                return null;
            }

            var method = typeof(BundleManager).GetMethod(nameof(BundleManager.ShouldAcquire),
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BundleItem) }, null);
            if (method == null || method.ReturnType != typeof(Task<bool>))
            {
                reason = "BundleManager.ShouldAcquire(BundleItem) has an unexpected signature.";
                return null;
            }

            reason = null;
            return method;
        }

        private static bool ShouldAcquirePrefix(BundleItem bundle, ref Task<bool> __result)
        {
            // In local mode SPT only checks that the file exists (no CRC), keep the original.
            if (RequestHandler.IsLocal)
            {
                return true;
            }

            __result = BundleVerifier.ShouldAcquire(bundle);
            return false;
        }
    }

    internal static class BundleVerifier
    {
        private class Entry
        {
            public long Size;
            public long Mtime;
            public uint Crc;
        }

        private static string _cachePath;
        private static bool _ignoreCache;
        private static SemaphoreSlim _gate;
        private static Dictionary<string, Entry> _oldEntries = new Dictionary<string, Entry>();
        private static readonly ConcurrentDictionary<string, Entry> NewEntries = new ConcurrentDictionary<string, Entry>();
        private static readonly ConcurrentDictionary<string, Task<bool>> Results = new ConcurrentDictionary<string, Task<bool>>();
        private static Task _scheduled;
        private static readonly object ScheduleLock = new object();

        private static int _hits;
        private static int _hashed;
        private static int _missing;
        private static int _mismatch;
        private static readonly Stopwatch Timer = new Stopwatch();

        public static void Init(string cachePath, bool ignoreCache, int maxThreads)
        {
            _cachePath = cachePath;
            _ignoreCache = ignoreCache;
            _gate = new SemaphoreSlim(maxThreads, maxThreads);

            try
            {
                if (!ignoreCache && File.Exists(cachePath))
                {
                    _oldEntries = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(cachePath))
                                  ?? new Dictionary<string, Entry>();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not read the cache, it will be rebuilt: {ex.Message}");
                _oldEntries = new Dictionary<string, Entry>();
            }

            Plugin.Log.LogInfo(ignoreCache
                ? "ForceFullVerify is on: every bundle will be re-hashed."
                : $"Cache loaded with {_oldEntries.Count} entries. Threads: {maxThreads}.");
        }

        public static async Task<bool> ShouldAcquire(BundleItem bundle)
        {
            try
            {
                EnsureScheduled();
                await _scheduled.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Batch verification failed, checking bundles one by one: {ex}");
            }

            if (Results.TryGetValue(bundle.FileName, out var task))
            {
                var acquire = await task;
                if (acquire)
                {
                    // It is about to be re-downloaded: a later query must verify the new file.
                    Results.TryRemove(bundle.FileName, out _);
                }

                return acquire;
            }

            // Not in the manifest when scheduling (or a re-query after a download): check on its own.
            return await Check(bundle);
        }

        private static void EnsureScheduled()
        {
            if (_scheduled != null)
            {
                return;
            }

            lock (ScheduleLock)
            {
                if (_scheduled != null)
                {
                    return;
                }

                Timer.Start();
                var bundles = BundleManager.Bundles.Values.ToArray();
                _scheduled = Task.Run(() =>
                {
                    foreach (var b in bundles)
                    {
                        Results.TryAdd(b.FileName, Check(b));
                    }

                    Task.WhenAll(Results.Values).ContinueWith(_ => Finish(bundles.Length));
                });
            }
        }

        private static Task<bool> Check(BundleItem bundle)
        {
            string path;
            FileInfo fi;
            try
            {
                path = BundleManager.GetBundleFilePath(bundle);
                fi = new FileInfo(path);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Bad bundle path, will re-download {bundle.FileName}: {ex.Message}");
                return Task.FromResult(true);
            }

            if (!fi.Exists)
            {
                Interlocked.Increment(ref _missing);
                Plugin.Log.LogInfo($"Missing from cache, will download: {bundle.FileName}");
                return Task.FromResult(true);
            }

            var size = fi.Length;
            var mtime = fi.LastWriteTimeUtc.Ticks;

            if (!_ignoreCache
                && _oldEntries.TryGetValue(bundle.FileName, out var e)
                && e.Size == size && e.Mtime == mtime && e.Crc == bundle.Crc)
            {
                Interlocked.Increment(ref _hits);
                NewEntries[bundle.FileName] = e;
                return Task.FromResult(false);
            }

            return HashAndCompare(bundle, path, size, mtime);
        }

        private static async Task<bool> HashAndCompare(BundleItem bundle, string path, long size, long mtime)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var crc = FastCrc32.HashFile(path);
                Interlocked.Increment(ref _hashed);

                if (crc == bundle.Crc)
                {
                    NewEntries[bundle.FileName] = new Entry { Size = size, Mtime = mtime, Crc = crc };
                    return false;
                }

                Interlocked.Increment(ref _mismatch);
                NewEntries.TryRemove(bundle.FileName, out _);
                Plugin.Log.LogInfo($"CRC mismatch, will re-download: {bundle.FileName}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Error hashing {bundle.FileName}, will re-download: {ex.Message}");
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void Finish(int total)
        {
            Timer.Stop();
            Plugin.Log.LogInfo(
                $"Verified {total} bundles in {Timer.ElapsedMilliseconds} ms: {_hits} from cache, {_hashed} hashed, " +
                $"{_mismatch} CRC mismatch, {_missing} missing.");

            try
            {
                var tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(new SortedDictionary<string, Entry>(NewEntries)));
                if (File.Exists(_cachePath))
                {
                    File.Delete(_cachePath);
                }

                File.Move(tmp, _cachePath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not save the cache: {ex.Message}");
            }
        }
    }
}
