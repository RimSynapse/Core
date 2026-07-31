using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// RimWorld mod entry point. Initializes Harmony patches and provides
    /// the mod settings UI.
    /// </summary>
    public partial class RimSynapseMod : Mod
    {
        public static RimSynapseMod Instance { get; private set; }
        public RimSynapseSettings Settings { get; }

        private const string HarmonyId = "RimSynapse.Core";

        // Internal test state

        // Scrollable content
        private static Vector2 _scrollPosition;
        private static float _viewHeight = 1500f;

        public static SynapseModHandle ModHandle { get; private set; }

        public RimSynapseMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RimSynapseSettings>();

            RimSynapse.SynapseLogger.InitMainThread();

            // Before anything else, and before defs are parsed: a mod loaded ahead of its own
            // dependency resolves none of its types and dies without saying so. Reporting it here
            // puts the explanation above the resulting wall of "Could not find a type named ...".
            SynapseLoadOrderCheck.Verify();

            // Apply Harmony patches
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll();

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                ModHandle = SynapseCore.Register(
                    "rimsynapse.core",
                    "RimSynapse Core"
                );
                
                ModHandle.RegisterQueryType("storyteller_pacing", "Storyteller: Pacing", LlmCapabilities.Text);
                ModHandle.RegisterQueryType("storyteller_event_selection", "Storyteller: Event Selection", LlmCapabilities.Text);

                SynapseToolRegistry.EnsureInitialized();
                SynapseTemplateRegistry.Initialize();
            });

            // Start background services (keep-alive, model discovery, HttpClient, SessionLogger)
            // immediately — uses system timers, independent of game ticks.
            SynapseCore.Initialize();

            RimSynapse.SynapseLogger.Message("[RimSynapse] Core initialized. Harmony patches applied.");
        }

        public override string SettingsCategory() => "RimSynapse Core";
        private void DrawCapabilitiesRow(Listing_Standard listing, ref LlmCapabilities caps)
        {
            Rect rect = listing.GetRect(24f);
            rect.x += 10f; // indent slightly
            rect.width -= 10f;
            float width = rect.width / 4f;
            
            bool hasText = (caps & LlmCapabilities.Text) == LlmCapabilities.Text;
            bool hasImage = (caps & LlmCapabilities.Image) == LlmCapabilities.Image;
            bool hasVision = (caps & LlmCapabilities.Vision) == LlmCapabilities.Vision;
            bool hasAudio = (caps & LlmCapabilities.Audio) == LlmCapabilities.Audio;

            Widgets.CheckboxLabeled(new Rect(rect.x, rect.y, width - 10f, rect.height), "Text", ref hasText);
            Widgets.CheckboxLabeled(new Rect(rect.x + width, rect.y, width - 10f, rect.height), "Image", ref hasImage);
            Widgets.CheckboxLabeled(new Rect(rect.x + width * 2, rect.y, width - 10f, rect.height), "Vision", ref hasVision);
            Widgets.CheckboxLabeled(new Rect(rect.x + width * 3, rect.y, width - 10f, rect.height), "Audio", ref hasAudio);

            caps = LlmCapabilities.None;
            if (hasText) caps |= LlmCapabilities.Text;
            if (hasImage) caps |= LlmCapabilities.Image;
            if (hasVision) caps |= LlmCapabilities.Vision;
            if (hasAudio) caps |= LlmCapabilities.Audio;
        }

    }
}
