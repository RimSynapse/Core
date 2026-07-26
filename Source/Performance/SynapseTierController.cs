using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>Capability tier the mod operates at. Higher tiers spend more context.</summary>
    public enum SynapseCapabilityTier
    {
        Minimal = 0,
        Standard = 1,
        Rich = 2
    }

    /// <summary>Player-facing tier selection. Auto derives the tier from measurement.</summary>
    public enum SynapseTierMode
    {
        Auto = 0,
        Minimal = 1,
        Standard = 2,
        Rich = 3
    }

    /// <summary>What currently limits a work class's prompt size.</summary>
    public enum SynapseGoverningConstraint
    {
        /// <summary>The class latency target is reachable; context fills up to it.</summary>
        Latency = 0,
        /// <summary>The backend's floor exceeds the target; context fills floor × headroom.</summary>
        LatencyFloor = 1,
        /// <summary>Metered backend; token caps govern regardless of latency.</summary>
        Cost = 2
    }

    public struct SynapseOperatingPoint
    {
        public SynapseGoverningConstraint GovernedBy;
        public int LatencyTargetMs;
        public int MaxPromptTokens;
        public float FloorMs;
        public float MsPerToken;
    }

    /// <summary>
    /// Picks the capability tier and per-class operating points from measured latency.
    ///
    /// Start low, promote on evidence: with no samples the tier is Minimal regardless of
    /// how large the reported window is — the window is the ceiling, measurement sets the
    /// operating point. Promotion moves one step at a time behind a cooldown; demotion is
    /// immediate, because sluggishness is the one thing capability must never buy.
    ///
    /// The per-class operating point implements the objective switch: when a backend's
    /// floor beats the class target, an unmetered backend relaxes the target to
    /// floor × (1 + headroom) and spends the wait on context, while a metered backend is
    /// governed by token caps instead. Every cloud provider is metered by default; the
    /// experimental "ignore token costs" setting flips a metered backend to the unmetered
    /// objective and is logged whenever it takes effect.
    /// </summary>
    public static class SynapseTierController
    {
        /// <summary>Extra response time an unavoidable floor is allowed to cost when scaling context up.</summary>
        public const float FloorHeadroom = 0.15f;

        // Window ceilings: a tier is unreachable without at least this much context.
        public const int StandardMinWindow = 8192;
        public const int RichMinWindow = 16384;

        // Responsiveness bands (p95 across all classes) gating promotion.
        public const float RichMaxP95Ms = 2000f;
        public const float StandardMaxP95Ms = 5000f;

        private const int MinSamplesToPromote = 8;
        private static readonly TimeSpan PromoteCooldown = TimeSpan.FromSeconds(30);

        private static SynapseCapabilityTier _autoTier = SynapseCapabilityTier.Minimal;
        private static DateTime _lastChange = DateTime.MinValue;
        private static int _lastSeenWindow = -1;
        private static bool _loggedCostOverride;

        /// <summary>The effective tier: manual override when set, otherwise the measured one.</summary>
        public static SynapseCapabilityTier Current
        {
            get
            {
                switch (Mode)
                {
                    case SynapseTierMode.Minimal: return SynapseCapabilityTier.Minimal;
                    case SynapseTierMode.Standard: return SynapseCapabilityTier.Standard;
                    case SynapseTierMode.Rich: return SynapseCapabilityTier.Rich;
                    default: return _autoTier;
                }
            }
        }

        public static SynapseTierMode Mode
        {
            get
            {
                int raw = RimSynapseMod.Instance?.Settings?.agentTierMode ?? 0;
                return raw >= 0 && raw <= 3 ? (SynapseTierMode)raw : SynapseTierMode.Auto;
            }
        }

        /// <summary>Budget multiplier consumed by QueryBudgetProfile — the formerly-documented performanceScalar.</summary>
        public static float PerformanceScalar
        {
            get
            {
                switch (Current)
                {
                    case SynapseCapabilityTier.Rich: return 1.4f;
                    case SynapseCapabilityTier.Standard: return 1.0f;
                    default: return 0.6f;
                }
            }
        }

        public static int EffectiveWindow =>
            Internal.ModelManager.ContextLength ?? (RimSynapseMod.Instance?.Settings?.modelContextLimit ?? 4096);

        /// <summary>
        /// Cloud providers are metered by default — no per-provider pricing knowledge, cloud
        /// simply means cost-governed. Local backends and Custom (typically a LAN server; the
        /// URL being non-localhost does not make it a paid API) are not.
        /// </summary>
        public static bool IsBackendMetered()
        {
            var provider = RimSynapseMod.Instance?.Settings?.apiProvider ?? ApiProvider.Local_LMStudio;
            switch (provider)
            {
                case ApiProvider.Google_Gemini:
                case ApiProvider.OpenAI:
                case ApiProvider.Anthropic_Claude:
                case ApiProvider.Pollinations:
                case ApiProvider.ElevenLabs:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Re-evaluate the tier. Called periodically from the game component; force skips the
        /// promotion cooldown (tests, and explicit "recalibrate now" actions).
        /// </summary>
        public static void Update(bool force = false)
        {
            int window = EffectiveWindow;

            // A different window means a different model — old timings are meaningless.
            if (_lastSeenWindow > 0 && window != _lastSeenWindow)
            {
                SynapsePerformanceModel.Reset($"context window changed {_lastSeenWindow} -> {window}");
                if (_autoTier != SynapseCapabilityTier.Minimal)
                {
                    Log(_autoTier, SynapseCapabilityTier.Minimal, $"model swap (window {window})");
                    _autoTier = SynapseCapabilityTier.Minimal;
                }
                _lastChange = DateTime.UtcNow;
            }
            _lastSeenWindow = window;

            var target = ComputeAutoTarget(window, out string trigger);

            if (target < _autoTier)
            {
                // Demote immediately: latency is the veto.
                Log(_autoTier, target, trigger);
                _autoTier = target;
                _lastChange = DateTime.UtcNow;
            }
            else if (target > _autoTier)
            {
                // Promote one step at a time, behind a cooldown, and only on real evidence.
                if (force || DateTime.UtcNow - _lastChange >= PromoteCooldown)
                {
                    var next = _autoTier + 1;
                    Log(_autoTier, next, trigger);
                    _autoTier = next;
                    _lastChange = DateTime.UtcNow;
                }
            }

            LogOperatingPoints();
        }

        private static SynapseCapabilityTier ComputeAutoTarget(int window, out string trigger)
        {
            int samples = SynapsePerformanceModel.TotalSampleCount();
            if (samples < MinSamplesToPromote)
            {
                trigger = $"insufficient evidence ({samples} samples)";
                return SynapseCapabilityTier.Minimal;
            }

            float p95 = SynapsePerformanceModel.P95All();
            trigger = $"p95={p95:F0}ms, window={window}, samples={samples}";

            if (p95 <= RichMaxP95Ms && window >= RichMinWindow) return SynapseCapabilityTier.Rich;
            if (p95 <= StandardMaxP95Ms && window >= StandardMinWindow) return SynapseCapabilityTier.Standard;
            return SynapseCapabilityTier.Minimal;
        }

        /// <summary>
        /// The sizing decision for one work class — the objective switch made concrete.
        /// </summary>
        public static SynapseOperatingPoint GetOperatingPoint(string eventType)
        {
            var settings = RimSynapseMod.Instance?.Settings;
            int slo = GetLatencyTargetMs(eventType);
            int windowCap = Math.Max(256, EffectiveWindow - Math.Max(512, (int)(EffectiveWindow * 0.25f)));

            var point = new SynapseOperatingPoint
            {
                GovernedBy = SynapseGoverningConstraint.Latency,
                LatencyTargetMs = slo,
                MaxPromptTokens = windowCap
            };

            bool haveCurve = SynapsePerformanceModel.TryEstimateCurve(eventType, out float floor, out float slope);
            point.FloorMs = floor;
            point.MsPerToken = slope;

            bool metered = IsBackendMetered();
            bool ignoreCosts = settings?.ignoreTokenCosts ?? false;

            if (metered && !ignoreCosts)
            {
                // Cost governs: latency is whatever the API gives us; tokens are the budget.
                int perRequest = Math.Max(256, settings?.tokenCapPerRequest ?? 4000);
                int dailyRemaining = Math.Max(0, (settings?.tokenCapPerDay ?? 200000) - SynapsePerformanceModel.TokensSpentToday);
                point.GovernedBy = SynapseGoverningConstraint.Cost;
                point.MaxPromptTokens = Math.Max(256, Math.Min(windowCap, Math.Min(perRequest, dailyRemaining)));
                return point;
            }

            if (metered && ignoreCosts && !_loggedCostOverride)
            {
                _loggedCostOverride = true;
                SynapseLogger.Warning("[Tier] Experimental 'ignore token costs' is ON for a metered backend — API spend is ungoverned.", "performance");
            }

            if (haveCurve && floor > slo)
            {
                // The target is unreachable: spend the unavoidable wait on context instead.
                point.GovernedBy = SynapseGoverningConstraint.LatencyFloor;
                point.LatencyTargetMs = (int)(floor * (1f + FloorHeadroom));
                point.MaxPromptTokens = slope > 0.01f
                    ? Math.Min(windowCap, Math.Max(256, (int)(floor * FloorHeadroom / slope)))
                    : windowCap;
                return point;
            }

            if (haveCurve && slope > 0.01f)
            {
                // Reachable target on a prefill-bound backend: fit tokens under the SLO.
                point.MaxPromptTokens = Math.Min(windowCap, Math.Max(256, (int)((slo - floor) / slope)));
            }

            return point;
        }

        /// <summary>
        /// Per-class latency target. XML profiles (SynapseContextProfileDef.latencyTargetMs)
        /// override; otherwise conversations answer fast and background classes get patience.
        /// </summary>
        public static int GetLatencyTargetMs(string eventType)
        {
            int fromDef = Internal.QueryBudgetProfile.GetProfileLatencyTargetMs(eventType);
            if (fromDef > 0) return fromDef;

            switch (eventType)
            {
                case "dialogue": return 500;
                case "reaction": return 800;
                case "thought": return 1500;
                case "relationship": return 1500;
                case "event": return 3000;
                case "quest": return 5000;
                default: return 2000;
            }
        }

        /// <summary>Test hook: reset controller state so cases start from a known point.</summary>
        public static void ResetForTesting()
        {
            _autoTier = SynapseCapabilityTier.Minimal;
            _lastChange = DateTime.MinValue;
            _lastSeenWindow = -1;
            _loggedCostOverride = false;
            SynapsePerformanceModel.Reset("test reset");
        }

        private static void Log(SynapseCapabilityTier from, SynapseCapabilityTier to, string trigger)
        {
            SynapseLogger.Message($"[Tier] {from} -> {to} ({trigger})", "performance");
        }

        private static readonly List<string> _classesToReport = new List<string> { "dialogue", "thought", "event", "custom" };
        private static DateTime _lastPointLog = DateTime.MinValue;

        private static void LogOperatingPoints()
        {
            // Throttled: the calibration should be inspectable, not noisy.
            if (DateTime.UtcNow - _lastPointLog < TimeSpan.FromSeconds(60)) return;

            bool any = false;
            foreach (var cls in _classesToReport)
            {
                if (SynapsePerformanceModel.SampleCount(cls) < 3) continue;
                var p = GetOperatingPoint(cls);
                SynapseLogger.Message(
                    $"[Tier] {cls}: governed={p.GovernedBy} floor={p.FloorMs:F0}ms slope={p.MsPerToken:F3}ms/tok target={p.LatencyTargetMs}ms maxPrompt={p.MaxPromptTokens}",
                    "performance");
                any = true;
            }
            if (any) _lastPointLog = DateTime.UtcNow;
        }
    }
}
