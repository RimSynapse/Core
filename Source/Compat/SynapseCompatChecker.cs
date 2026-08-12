using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace RimSynapse.Compat
{
    /// <summary>
    /// Cross-mod compatibility tracker (Core #91). At load it reads every active RimSynapse mod's
    /// <c>About/Manifest.xml</c> and warns — in the log and via a one-time dialog — when a module
    /// requires a newer Core than is installed. The check is fully local and deterministic; the
    /// opt-in "update available" check (Steam Workshop / GitHub) is a separate slice.
    ///
    /// This lives in Core because Core is the version everything else pins to. Companion mods that
    /// run without Core (e.g. Regions-and-Territories) ship their own minimal self-tracker instead.
    /// </summary>
    public static class SynapseCompatChecker
    {
        public sealed class Incompatibility
        {
            public SynapseManifest Mod;
            public SynapseVersion Required;
            public SynapseVersion InstalledCore;
        }

        public sealed class CompatReport
        {
            public readonly List<SynapseManifest> Manifests = new List<SynapseManifest>();
            public readonly List<string> MissingManifest = new List<string>();
            public readonly List<Incompatibility> Incompatibilities = new List<Incompatibility>();
            public SynapseVersion InstalledCore;
        }

        /// <summary>Reads all RimSynapse manifests and evaluates Core-floor compatibility. Never throws.</summary>
        public static CompatReport Check()
        {
            var report = new CompatReport();

            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                string pid = mod?.PackageId;
                if (string.IsNullOrEmpty(pid) || !pid.StartsWith("rimsynapse.", StringComparison.OrdinalIgnoreCase))
                    continue;

                SynapseManifest mani = SynapseManifest.Load(mod.RootDir, pid, mod.Name);
                if (mani == null) report.MissingManifest.Add($"{mod.Name} ({pid})");
                else report.Manifests.Add(mani);
            }

            SynapseManifest core = report.Manifests.FirstOrDefault(m => m.IsCore);
            report.InstalledCore = core?.Version;

            if (report.InstalledCore != null)
            {
                foreach (SynapseManifest m in report.Manifests)
                {
                    if (m.IsCore || m.CoreFloor == null) continue;
                    if (report.InstalledCore.CompareTo(m.CoreFloor) < 0)
                    {
                        report.Incompatibilities.Add(new Incompatibility
                        {
                            Mod = m,
                            Required = m.CoreFloor,
                            InstalledCore = report.InstalledCore,
                        });
                    }
                }
            }

            return report;
        }

        /// <summary>Runs the local check at startup: logs a one-line summary and, on any incompatibility, a warning dialog.</summary>
        public static void RunStartupCheck()
        {
            CompatReport report = Check();

            SynapseLogger.Message(
                $"[RimSynapse] Compat: {report.Manifests.Count} RimSynapse mod(s) with a manifest, " +
                $"Core {(report.InstalledCore?.ToString() ?? "unknown")}, " +
                $"{report.Incompatibilities.Count} incompatibility(ies), " +
                $"{report.MissingManifest.Count} without a manifest.");

            if (report.Incompatibilities.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("RimSynapse detected version incompatibilities:");
            sb.AppendLine();
            foreach (Incompatibility inc in report.Incompatibilities)
                sb.AppendLine($"• {inc.Mod.ModName} needs Core {inc.Required} or newer, but Core {inc.InstalledCore} is installed.");
            sb.AppendLine();
            sb.AppendLine("Update RimSynapse Core (and any out-of-date modules) so their versions match.");

            string msg = sb.ToString();
            SynapseLogger.Warning("[RimSynapse] " + msg.Replace(Environment.NewLine, " "));
            Find.WindowStack?.Add(new Dialog_MessageBox(msg));
        }

        /// <summary>Human-readable dump of the current report, for the debug action / headless validation.</summary>
        public static string DumpReport()
        {
            CompatReport r = Check();
            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Compatibility report — installed Core: {(r.InstalledCore?.ToString() ?? "unknown")}");

            sb.AppendLine($"  RimSynapse mods with a manifest ({r.Manifests.Count}):");
            foreach (SynapseManifest m in r.Manifests.OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                string floor = m.CoreFloor != null ? $" — needs Core >= {m.CoreFloor}" : "";
                string core = m.IsCore ? " [CORE]" : "";
                sb.AppendLine($"    {m.ModName} ({m.PackageId}) v{m.Version}{floor}{core}");
            }

            if (r.MissingManifest.Count > 0)
            {
                sb.AppendLine($"  RimSynapse mods WITHOUT a manifest ({r.MissingManifest.Count}) — tracker not yet integrated:");
                foreach (string s in r.MissingManifest) sb.AppendLine($"    {s}");
            }

            sb.AppendLine($"  Incompatibilities ({r.Incompatibilities.Count}):");
            foreach (Incompatibility inc in r.Incompatibilities)
                sb.AppendLine($"    {inc.Mod.ModName} needs Core >= {inc.Required}, installed {inc.InstalledCore}");

            return sb.ToString();
        }
    }

    /// <summary>Fires the local compat check once, after startup finishes, when the window stack exists.</summary>
    [StaticConstructorOnStartup]
    internal static class SynapseCompatStartup
    {
        static SynapseCompatStartup()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try { SynapseCompatChecker.RunStartupCheck(); }
                catch (Exception e) { SynapseLogger.Error("[RimSynapse] Compat startup check failed: " + e); }
            });
        }
    }
}
