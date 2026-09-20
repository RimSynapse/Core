using System;
using System.Threading;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Background keep-alive ping timer. Sends a minimal 1-token request
    /// to LM Studio every 4 minutes to prevent model unloading.
    /// </summary>
    internal static class KeepAlive
    {
        private static Timer _timer;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(4);

        // Re-poll model info (incl. the loaded context window) on this cadence so a model reload or
        // n_ctx change is picked up mid-session instead of freezing the window read at startup — the
        // "Minimal forever" trap (Core #139). Rides the keep-alive tick; a manual refresh or an
        // earlier signal (startup poll) can update it sooner.
        private static readonly TimeSpan ModelRefreshInterval = TimeSpan.FromMinutes(30);
        private static DateTime _lastModelRefresh = DateTime.MinValue;

        /// <summary>
        /// Start the keep-alive timer. Safe to call multiple times.
        /// </summary>
        internal static void Start()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null || !settings.enableKeepAlive) return;

            if (_timer != null) return;

            _timer = new Timer(OnTick, null, Interval, Interval);
            SynapseLogger.Message("Keep-alive timer started (every 4 minutes).");
        }

        /// <summary>
        /// Stop the keep-alive timer.
        /// </summary>
        internal static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static void OnTick(object state)
        {
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null || !settings.enableKeepAlive)
            {
                Stop();
                return;
            }

            // Periodically re-read model info so a reloaded model / changed context window is picked
            // up (~30 min cadence). RefreshCache re-queries /api/v0 loaded_context_length; the tier
            // controller re-budgets to it on its next update. TierController.Update never demotes on a
            // window change now, so a bigger window promotes rather than sticking at stale quality.
            if (DateTime.UtcNow - _lastModelRefresh >= ModelRefreshInterval)
            {
                _lastModelRefresh = DateTime.UtcNow;
                ModelManager.RefreshCache();
                return;
            }

            string model = ModelManager.ActiveModel;
            if (string.IsNullOrEmpty(model))
            {
                // No active model — try refreshing
                ModelManager.RefreshCache();
                return;
            }

            HttpEngine.SendKeepAlivePing(model);
        }

        /// <summary>
        /// Shutdown alias.
        /// </summary>
        internal static void Shutdown() => Stop();
    }
}
