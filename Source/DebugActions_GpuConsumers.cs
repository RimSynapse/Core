using System.Linq;
using System.Text;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the GpuStats in-process consumers channel (Core #104), grouped under
    /// "RimSynapse". Registers a throwaway consumer through the public API and dumps the snapshot a
    /// monitor mod (the NVIDIA Tool) would read, then removes it so it leaves no residue. Headlessly
    /// runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_GpuConsumers
    {
        private const string ProbeId = "rimsynapse.debugprobe";

        [DebugAction("RimSynapse", "GPU consumers: register a probe + dump snapshot",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void RegisterProbeAndDump()
        {
            var gpu = SynapseClient.Gpu;
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] GPU in-process consumers (Core #104):");

            int before = gpu.ConsumersSnapshot().Count;

            // Upsert twice with the same modId to prove update-in-place (not append), then flip to
            // non-resident to prove that reports 0 VRAM.
            gpu.UpsertConsumer(ProbeId, "Debug Probe", 400f, resident: true);
            gpu.UpsertConsumer(ProbeId, "Debug Probe (Kokoro)", 512f, resident: true);
            var afterUpsert = gpu.ConsumersSnapshot();
            var probe = afterUpsert.FirstOrDefault(c => c.modId == ProbeId);
            sb.AppendLine($"  after upsert: {before} -> {afterUpsert.Count} consumer(s); probe = " +
                          (probe != null ? $"'{probe.label}' {probe.vramMb:F0} MB resident={probe.resident}" : "(missing!)"));

            gpu.UpsertConsumer(ProbeId, "Debug Probe (Kokoro)", 512f, resident: false);
            probe = gpu.ConsumersSnapshot().FirstOrDefault(c => c.modId == ProbeId);
            sb.AppendLine($"  non-resident probe reports vram = {probe?.vramMb:F0} MB (expected 0)");

            sb.AppendLine("  full snapshot a monitor would render:");
            foreach (var c in gpu.ConsumersSnapshot())
                sb.AppendLine($"    - [{c.modId}] {c.label}: {c.vramMb:F0} MB (resident={c.resident})");

            // Leave no residue: remove the probe entirely so a real monitor never shows the debug line.
            gpu.RemoveConsumer(ProbeId);
            sb.AppendLine($"  probe removed; consumers now: {gpu.ConsumersSnapshot().Count}");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
