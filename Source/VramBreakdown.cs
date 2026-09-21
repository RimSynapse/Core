using System;
using System.Collections.Generic;
using UnityEngine;

namespace RimSynapse
{
    /// <summary>
    /// Attributes the GPU's dedicated VRAM in use to the things using it, for the monitor's Advanced
    /// view (Core #128). Vendor-neutral — the total-in-use figure comes from the cross-vendor
    /// <see cref="VramMeter"/> (Core #125), never a vendor library:
    ///
    ///   RimWorld   → Unity's own <c>Texture.currentTextureMemory</c> plus a fixed overhead factor.
    ///   LM Studio  → estimated from the loaded model's parameter count (<see cref="VramAdvisor"/>),
    ///                and only when the endpoint is local — a remote host runs on another GPU.
    ///   consumers  → in-process models registered through the GpuStats channel (Core #104), e.g.
    ///                Local TTS's Kokoro, which no per-process enumeration can see separately.
    ///   system     → whatever measured usage is left after the above (desktop, compositor, others).
    ///
    /// When the meter is unsupported (integrated GPU, non-Windows) the measured total is unknown, so
    /// the system line is withheld and <see cref="Measured"/> is false; the component estimates that
    /// don't need a measured total (RimWorld, LM Studio, consumers) still populate.
    /// </summary>
    internal static class VramBreakdown
    {
        private static float _totalUsedMb;   // measured system-wide used (0 when unmeasured)
        private static float _rimworldMb;
        private static float _lmStudioMb;    // resident portion attributed to this GPU
        private static float _consumersMb;
        private static float _systemMb;
        private static bool _measured;
        private static bool _lmStudioRemote;
        private static List<GpuMemoryConsumer> _consumers = new List<GpuMemoryConsumer>();
        private static DateTime _lastUpdate = DateTime.MinValue;
        private const float UpdateIntervalSec = 2f;

        internal static bool Measured => _measured;
        internal static float TotalUsedMb => _totalUsedMb;
        internal static float RimWorldMb => _rimworldMb;
        internal static float LmStudioMb => _lmStudioMb;
        internal static float ConsumersMb => _consumersMb;
        internal static float SystemMb => _systemMb;
        internal static bool LmStudioRemote => _lmStudioRemote;
        internal static List<GpuMemoryConsumer> Consumers => _consumers;

        /// <summary>Refresh the breakdown (throttled internally). Safe to call every frame.</summary>
        internal static void Refresh()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUpdate).TotalSeconds < UpdateIntervalSec) return;
            _lastUpdate = now;

            _measured = VramMeter.Sample();
            _totalUsedMb = _measured ? VramMeter.UsedMb : 0f;

            _rimworldMb = GetRimWorldVramMb();

            _lmStudioRemote = IsLmStudioRemote();
            _lmStudioMb = _lmStudioRemote ? 0f : EstimateLmStudioMb();

            _consumers = GatherConsumers();
            _consumersMb = 0f;
            foreach (var c in _consumers) _consumersMb += c.vramMb;

            // System is the measured remainder. Without a measured total there is nothing to take a
            // remainder from, so leave it at 0 and let the view show the components only.
            if (_measured)
            {
                float rest = _totalUsedMb - _rimworldMb - _lmStudioMb - _consumersMb;
                _systemMb = rest > 0f ? rest : 0f;
            }
            else
            {
                _systemMb = 0f;
            }
        }

        private static float GetRimWorldVramMb()
        {
            try
            {
                // currentTextureMemory is GPU-resident texture bytes; add a flat overhead for render
                // targets, shader/constant buffers and mesh buffers. RimWorld is 2D-heavy, so 40% is a
                // conservative multiplier. Floor at 50 MB — even a minimal scene uses some.
                float texMb = (long)Texture.currentTextureMemory / (1024f * 1024f);
                float est = texMb * 1.4f;
                return est < 50f ? 50f : est;
            }
            catch { return 200f; }
        }

        private static float EstimateLmStudioMb()
        {
            try
            {
                string model = SynapseClient.ActiveModelName;
                if (string.IsNullOrEmpty(model))
                    model = RimSynapseMod.Instance?.Settings?.selectedModel;
                float gb = VramAdvisor.EstimateModelVramGb(model);
                return gb > 0f ? gb * 1024f : 0f;
            }
            catch { return 0f; }
        }

        private static bool IsLmStudioRemote()
        {
            try { return RimSynapseMod.Instance?.Settings?.IsRemoteUrl ?? false; }
            catch { return false; }
        }

        private static List<GpuMemoryConsumer> GatherConsumers()
        {
            var result = new List<GpuMemoryConsumer>();
            try
            {
                var snapshot = SynapseClient.Gpu?.ConsumersSnapshot();
                if (snapshot != null)
                    foreach (var c in snapshot)
                        if (c != null && c.resident && c.vramMb > 0f)
                            result.Add(c);
            }
            catch { /* older state without the channel — nothing to add */ }
            return result;
        }
    }
}
