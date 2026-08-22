using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// GPU statistics. Populated by an external GPU monitor mod via
    /// <see cref="SynapseClient.Gpu"/>. RimSynapse Core provides the
    /// framework — a separate mod does the actual polling.
    /// </summary>
    public class GpuStats
    {
        /// <summary>Whether GPU monitoring is supported and active.</summary>
        public bool supported;

        /// <summary>GPU core utilization (0-100%).</summary>
        public int utilizationPercent;

        /// <summary>Currently used VRAM in GB.</summary>
        public float usedVramGb;

        /// <summary>Total VRAM capacity in GB.</summary>
        public float totalVramGb;

        /// <summary>Optional per-process VRAM breakdown.</summary>
        public List<GpuProcess> processes = new List<GpuProcess>();

        /// <summary>
        /// In-process GPU-memory consumers registered by other RimSynapse mods (e.g. an on-device
        /// model loaded into VRAM inside RimWorld's own process). Unlike <see cref="processes"/>,
        /// these are not separate OS processes — their VRAM is part of RimWorld's process footprint
        /// and would otherwise be indistinguishable. A monitor mod can surface them as their own
        /// breakdown lines. Keyed by <see cref="GpuMemoryConsumer.modId"/> via <see cref="UpsertConsumer"/>.
        /// </summary>
        public List<GpuMemoryConsumer> consumers = new List<GpuMemoryConsumer>();

        private readonly object _consumerLock = new object();

        /// <summary>
        /// Insert or update (by modId) an in-process VRAM consumer. Thread-safe — the reporting mod
        /// typically calls this from a background thread while a monitor reads from the UI thread.
        /// A consumer with <paramref name="resident"/> false (e.g. running on CPU) reports 0 VRAM.
        /// </summary>
        public void UpsertConsumer(string modId, string label, float vramMb, bool resident)
        {
            if (string.IsNullOrEmpty(modId)) return;
            lock (_consumerLock)
            {
                var existing = consumers.Find(c => c.modId == modId);
                if (existing == null)
                {
                    existing = new GpuMemoryConsumer { modId = modId };
                    consumers.Add(existing);
                }
                existing.label = label;
                existing.vramMb = resident ? vramMb : 0f;
                existing.resident = resident;
                existing.lastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>Snapshot of the currently registered consumers (thread-safe copy).</summary>
        public List<GpuMemoryConsumer> ConsumersSnapshot()
        {
            lock (_consumerLock)
            {
                return new List<GpuMemoryConsumer>(consumers);
            }
        }

        /// <summary>
        /// Remove a consumer by modId. Thread-safe. Reporting a non-resident consumer via
        /// <see cref="UpsertConsumer"/> (0 VRAM) is the usual "model unloaded" signal; use this only
        /// when a mod wants its row gone entirely (e.g. it is shutting down). No-op if absent.
        /// </summary>
        public void RemoveConsumer(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;
            lock (_consumerLock)
            {
                consumers.RemoveAll(c => c.modId == modId);
            }
        }

        /// <summary>When the stats were last updated.</summary>
        public DateTime lastUpdated;

        /// <summary>VRAM usage as a percentage (0.0 - 1.0).</summary>
        public float VramUsagePercent =>
            totalVramGb > 0f ? usedVramGb / totalVramGb : 0f;
    }

    /// <summary>
    /// An in-process consumer of GPU VRAM registered by a RimSynapse mod (e.g. a neural model
    /// loaded into VRAM inside RimWorld's process). Reported through <see cref="GpuStats.UpsertConsumer"/>.
    /// </summary>
    public class GpuMemoryConsumer
    {
        /// <summary>Stable id of the reporting mod (e.g. "rimsynapse.localtts").</summary>
        public string modId;

        /// <summary>Human-readable label for the breakdown line (e.g. "Local TTS (Kokoro)").</summary>
        public string label;

        /// <summary>Estimated VRAM footprint in MB (0 when not resident on the GPU).</summary>
        public float vramMb;

        /// <summary>True when the consumer is actually resident on the GPU (vs. running on CPU).</summary>
        public bool resident;

        /// <summary>When this consumer last reported.</summary>
        public DateTime lastUpdated;
    }

    /// <summary>
    /// A single process consuming GPU VRAM.
    /// </summary>
    public class GpuProcess
    {
        /// <summary>Process ID.</summary>
        public int pid;

        /// <summary>Process name (e.g., "RimWorld", "LM Studio").</summary>
        public string name;

        /// <summary>VRAM usage in MB.</summary>
        public float vramMb;
    }
}
