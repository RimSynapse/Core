using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RimSynapse
{
    /// <summary>
    /// Cross-vendor, measured VRAM meter (Core #125). Reports how much of the primary GPU's
    /// dedicated memory is in use <em>system-wide</em> — RimWorld, an on-machine LM Studio, the
    /// desktop compositor, everything — so the VRAM advisory and the monitor's Advanced view can
    /// show a real free-headroom number instead of a hardcoded estimate.
    ///
    /// Source of truth is the Windows PDH performance counter
    /// <c>\GPU Adapter Memory(*)\Dedicated Usage</c>, which the OS aggregates across processes for
    /// every GPU vendor (NVIDIA, AMD, Intel). It is plain C P/Invoke into <c>pdh.dll</c> — no
    /// vendor DLL and no COM, so it works under RimWorld's Mono runtime where DXGI's COM interface
    /// would be unreliable. Total capacity comes from Unity's <see cref="SystemInfo.graphicsMemorySize"/>.
    ///
    /// Everything degrades quietly: on any non-Windows host, or if PDH is unavailable or returns
    /// nothing, <see cref="Supported"/> stays false and callers fall back to the estimate. A missing
    /// counter is expected on Windows editions older than 10, so it is logged once, not per sample.
    /// </summary>
    public static class VramMeter
    {
        /// <summary>Where the current <see cref="UsedMb"/> figure came from.</summary>
        public enum MeterSource { None, Pdh }

        // ── Cached sample (all guarded by _lock) ──
        private static readonly object _lock = new object();
        private static float _totalMb;
        private static float _usedMb;
        private static MeterSource _source = MeterSource.None;
        private static DateTime _lastSample = DateTime.MinValue;
        private static string _lastError;

        /// <summary>Minimum wall-clock gap between real PDH queries. Reads are throttled so the
        /// monitor's per-frame refresh and the advisory can both call <see cref="Sample"/> freely.</summary>
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

        /// <summary>Whether the last sample produced a measured, trustworthy used-VRAM figure.</summary>
        public static bool Supported
        {
            get { lock (_lock) { return _source != MeterSource.None && _totalMb > 0f; } }
        }

        /// <summary>Total dedicated VRAM in MB (from Unity's SystemInfo; 0 if unknown).</summary>
        public static float TotalMb { get { lock (_lock) { return _totalMb; } } }

        /// <summary>Measured system-wide dedicated VRAM in use, in MB. Meaningful only when
        /// <see cref="Supported"/> is true.</summary>
        public static float UsedMb { get { lock (_lock) { return _usedMb; } } }

        /// <summary>Measured free VRAM in MB (total − used), clamped to ≥ 0.</summary>
        public static float FreeMb
        {
            get { lock (_lock) { float f = _totalMb - _usedMb; return f > 0f ? f : 0f; } }
        }

        /// <summary>When the meter last took a real (non-throttled) sample.</summary>
        public static DateTime LastSample { get { lock (_lock) { return _lastSample; } } }

        /// <summary>Last error text if a sample failed, else null.</summary>
        public static string LastError { get { lock (_lock) { return _lastError; } } }

        /// <summary>
        /// Take a sample if enough time has passed since the last one, otherwise return the cached
        /// values. Safe to call from the UI thread every frame. Returns <see cref="Supported"/>.
        /// </summary>
        public static bool Sample()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastSample < MinInterval && _lastSample != DateTime.MinValue)
                    return _source != MeterSource.None && _totalMb > 0f;
            }
            return SampleNow();
        }

        /// <summary>Force an immediate sample, bypassing the throttle. Used by the debug action.</summary>
        public static bool SampleNow()
        {
            float totalMb = 0f;
            try { int mb = SystemInfo.graphicsMemorySize; if (mb > 0) totalMb = mb; }
            catch { /* SystemInfo unavailable — leave total at 0 */ }

            float usedMb = 0f;
            MeterSource source = MeterSource.None;
            string error = null;

            if (RuntimeIsWindows())
            {
                try
                {
                    if (TryReadDedicatedUsageBytes(out long usedBytes))
                    {
                        usedMb = usedBytes / (1024f * 1024f);
                        source = MeterSource.Pdh;
                    }
                }
                catch (DllNotFoundException ex) { error = "pdh.dll not found: " + ex.Message; }
                catch (Exception ex) { error = ex.Message; }
            }
            else
            {
                error = "non-Windows host — measured VRAM unavailable";
            }

            lock (_lock)
            {
                _totalMb = totalMb;
                _usedMb = usedMb;
                _source = source;
                _lastSample = DateTime.UtcNow;
                _lastError = error;
            }

            PushToGpuStats();

            if (source == MeterSource.None && !_loggedUnavailable)
            {
                _loggedUnavailable = true;
                SynapseLogger.Info("core",
                    "VRAM meter: measured used-VRAM unavailable (" + (error ?? "no GPU counter") +
                    ") — the advisory will fall back to an estimate.");
            }

            return source != MeterSource.None && totalMb > 0f;
        }

        private static bool _loggedUnavailable;

        /// <summary>Mirror the current sample into Core's GpuStats channel so any consumer that reads
        /// <see cref="SynapseClient.Gpu"/> sees the measured numbers.</summary>
        private static void PushToGpuStats()
        {
            var gpu = SynapseClient.Gpu;
            if (gpu == null) return;
            lock (_lock)
            {
                if (_source != MeterSource.None && _totalMb > 0f)
                {
                    gpu.supported = true;
                    gpu.usedVramGb = _usedMb / 1024f;
                    gpu.totalVramGb = _totalMb / 1024f;
                    gpu.lastUpdated = _lastSample;
                }
            }
        }

        // ────────────────────────────────────────────────────────
        //  PDH: \GPU Adapter Memory(*)\Dedicated Usage
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Query dedicated GPU memory in use, adapter-wide. The <c>GPU Adapter Memory</c> instances are
        /// per-adapter (<c>luid_..._phys_0</c>), not per-process, so <c>Dedicated Usage</c> is the
        /// system-wide dedicated VRAM committed on that adapter — RimWorld + LM Studio + desktop. We
        /// take the largest instance, which on a hybrid machine (integrated + discrete) is the discrete
        /// card the game and the model actually run on.
        ///
        /// Verified against the live counters on a shared-memory box: <c>Dedicated Usage</c> reads 0
        /// there while <c>Shared Usage</c>/<c>Total Committed</c> are populated — i.e. an integrated GPU
        /// has no dedicated VRAM. We deliberately report only dedicated: on an iGPU there is no VRAM
        /// ceiling to warn about (it spills into system RAM), so a 0 here correctly yields "unsupported"
        /// and the advisory falls back to its estimate. Discrete GPUs — the local-LLM audience — report
        /// real dedicated usage.
        /// </summary>
        private static bool TryReadDedicatedUsageBytes(out long usedBytes)
        {
            usedBytes = 0;
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != ERROR_SUCCESS || query == IntPtr.Zero)
                    return false;

                if (PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage",
                        IntPtr.Zero, out IntPtr counter) != ERROR_SUCCESS || counter == IntPtr.Zero)
                    return false;

                if (PdhCollectQueryData(query) != ERROR_SUCCESS)
                    return false;

                // First call sizes the buffer.
                uint bufSize = 0, itemCount = 0;
                uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_LARGE,
                    ref bufSize, out itemCount, IntPtr.Zero);
                if (status != PDH_MORE_DATA || bufSize == 0)
                    return false;

                IntPtr buffer = Marshal.AllocHGlobal((int)bufSize);
                try
                {
                    status = PdhGetFormattedCounterArray(counter, PDH_FMT_LARGE,
                        ref bufSize, out itemCount, buffer);
                    if (status != ERROR_SUCCESS || itemCount == 0)
                        return false;

                    int stride = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));
                    long best = 0;
                    for (int i = 0; i < itemCount; i++)
                    {
                        var item = (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(
                            IntPtr.Add(buffer, i * stride), typeof(PDH_FMT_COUNTERVALUE_ITEM));
                        if (item.CStatus == ERROR_SUCCESS && item.largeValue > best)
                            best = item.largeValue;
                    }
                    if (best <= 0) return false;
                    usedBytes = best;
                    return true;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally
            {
                if (query != IntPtr.Zero) PdhCloseQuery(query);
            }
        }

        private static bool RuntimeIsWindows()
        {
            // RimWorld ships a Windows build (the LLM-local userbase) plus Mac/Linux. PDH exists only
            // on Windows; elsewhere we never touch pdh.dll.
            PlatformID p = Environment.OSVersion.Platform;
            return p == PlatformID.Win32NT || p == PlatformID.Win32Windows || p == PlatformID.Win32S;
        }

        // ── pdh.dll P/Invoke ──

        private const uint ERROR_SUCCESS = 0;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_FMT_LARGE = 0x00000400;

        // x64 layout: LPWSTR szName @0 (8) + PDH_FMT_COUNTERVALUE { DWORD CStatus @8; 4 pad;
        // LONGLONG largeValue @16 } → 24 bytes. RimWorld is x64-only.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            [FieldOffset(0)] public IntPtr szName;
            [FieldOffset(8)] public uint CStatus;
            [FieldOffset(16)] public long largeValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath,
            IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr hCounter, uint dwFormat,
            ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);
    }
}
