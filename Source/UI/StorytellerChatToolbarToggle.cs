using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSynapse.UI
{
    /// <summary>
    /// Harmony postfix on PlaySettings.DoPlaySettingsGlobalControls that adds the player↔storyteller
    /// Chat window toggle to the settings toolbar row (Core #99).
    ///
    /// Gated on <see cref="SynapseStorytellerContext.IsRimSynapseStorytellerActive"/>: under a vanilla
    /// storyteller the icon is not drawn and the window cannot be opened, replacing the old brittle
    /// <c>defName == "Synapse"</c> check that broke when the storyteller was renamed to "Aura".
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    [StaticConstructorOnStartup]
    internal static class StorytellerChatToolbarToggle
    {
        private static readonly Texture2D _cachedIcon = GenerateChatIcon();

        [HarmonyPostfix]
        static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null) return;

            // Master gate: only a live RimSynapse storyteller exposes the chat window.
            if (!SynapseStorytellerContext.IsRimSynapseStorytellerActive) return;

            bool isOpen = Find.WindowStack.IsOpen<StorytellerConversationWindow>();
            bool wasOpen = isOpen;

            string storytellerLabel = Find.Storyteller?.def?.LabelCap ?? "the storyteller";
            row.ToggleableIcon(ref isOpen, _cachedIcon,
                $"Chat with {storytellerLabel}\n\n" +
                "Opens a direct conversation with the AI storyteller. She reads the log as sentiment on " +
                "her own schedule — talking to her never triggers an event by itself.",
                SoundDefOf.Mouseover_ButtonToggle);

            if (isOpen != wasOpen)
            {
                if (isOpen)
                {
                    Find.WindowStack.Add(new StorytellerConversationWindow());
                }
                else
                {
                    var window = Find.WindowStack.WindowOfType<StorytellerConversationWindow>();
                    window?.Close();
                }
            }
        }

        /// <summary>Draw a simple speech-bubble glyph (a rounded box with a tail and three dots)
        /// procedurally so the toggle needs no shipped texture asset.</summary>
        private static Texture2D GenerateChatIcon()
        {
            string[] mask = new string[]
            {
                "                        ",
                "                        ",
                "                        ",
                "   xxxxxxxxxxxxxxxxxx   ",
                "  xx              xx    ",
                "  xx              xx    ",
                "  xx  xx  xx  xx  xx    ",
                "  xx              xx    ",
                "  xx              xx    ",
                "  xx  xx  xx  xx  xx    ",
                "  xx              xx    ",
                "  xx              xx    ",
                "   xxxxxxxxxxxxxxxxxx   ",
                "     xx                 ",
                "   xx                   ",
                "                        ",
                "                        ",
                "                        ",
                "                        ",
                "                        ",
                "                        ",
                "                        ",
                "                        ",
                "                        "
            };
            return GenerateIconFromMask(mask);
        }

        private static Texture2D GenerateIconFromMask(string[] mask)
        {
            int size = 24;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Point;
            var pixels = new Color32[size * size];
            var clear = new Color32(0, 0, 0, 0);
            var body = new Color32(150, 150, 150, 255);
            var black = new Color32(0, 0, 0, 255);

            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            bool[,] isBody = new bool[size, size];
            for (int y = 0; y < size; y++)
            {
                int texY = size - 1 - y;
                if (y < mask.Length)
                {
                    for (int x = 0; x < size && x < mask[y].Length; x++)
                    {
                        if (mask[y][x] != ' ') isBody[x, texY] = true;
                    }
                }
            }

            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    if (isBody[x, y])
                    {
                        pixels[y * size + x] = body;
                    }
                    else
                    {
                        bool nearBody = false;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx >= 0 && nx < size && ny >= 0 && ny < size)
                                {
                                    if (isBody[nx, ny]) nearBody = true;
                                }
                            }
                        }
                        if (nearBody) pixels[y * size + x] = black;
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
