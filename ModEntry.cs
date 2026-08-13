using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;

namespace HarvestCombo;

/// <summary>The mod entry point.</summary>
public sealed class ModEntry : Mod
{
    private const double MinimumTimeoutSeconds = 0.1;

    private static readonly HashSet<int> Milestones = new() { 5, 10, 25, 50, 100 };

    private static ModEntry? Instance;

    private readonly PerScreen<ComboState> combo = new(() => new ComboState());

    private ModConfig config = new();

    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        Instance = this;
        this.config = helper.ReadConfig<ModConfig>();

        if (!double.IsFinite(this.config.ComboTimeoutSeconds) || this.config.ComboTimeoutSeconds < MinimumTimeoutSeconds)
        {
            this.Monitor.Log(
                $"ComboTimeoutSeconds must be at least {MinimumTimeoutSeconds:0.0}. The default value (2.0) will be used for this session.",
                LogLevel.Warn
            );
            this.config.ComboTimeoutSeconds = 2.0;
        }

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.Display.RenderedHud += this.OnRenderedHud;

        this.ApplyCropHarvestPatch();
    }

    private void ApplyCropHarvestPatch()
    {
        MethodInfo? harvestMethod = AccessTools.Method(
            typeof(Crop),
            nameof(Crop.harvest),
            new[] { typeof(int), typeof(int), typeof(HoeDirt), typeof(JunimoHarvester), typeof(bool) }
        );

        if (harvestMethod is null)
        {
            this.Monitor.Log("Couldn't find Crop.harvest. Harvest Combo has been disabled.", LogLevel.Error);
            return;
        }

        Harmony harmony = new(this.ModManifest.UniqueID);
        harmony.Patch(
            harvestMethod,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeCropHarvest)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterCropHarvest))
        );
    }

    /// <summary>Capture enough state to distinguish a successful harvest from a rejected interaction.</summary>
    private static void BeforeCropHarvest(Crop __instance, JunimoHarvester? junimoHarvester, out CropHarvestState __state)
    {
        bool isReady = junimoHarvester is null
            && Context.IsWorldReady
            && !__instance.dead.Value
            && !__instance.forageCrop.Value
            && __instance.currentPhase.Value >= __instance.phaseDays.Count - 1
            && (!__instance.fullyGrown.Value || __instance.dayOfCurrentPhase.Value <= 0);

        __state = new CropHarvestState(
            isReady,
            __instance.currentPhase.Value,
            __instance.dayOfCurrentPhase.Value,
            __instance.fullyGrown.Value
        );
    }

    /// <summary>Count the crop only if the game removed it or moved a regrowing crop back into its regrowth cycle.</summary>
    private static void AfterCropHarvest(Crop __instance, bool __result, CropHarvestState __state)
    {
        if (!__state.WasReady)
            return;

        bool cropStateChanged = __instance.currentPhase.Value != __state.CurrentPhase
            || __instance.dayOfCurrentPhase.Value != __state.DayOfCurrentPhase
            || __instance.fullyGrown.Value != __state.WasFullyGrown;

        if (__result || cropStateChanged)
            Instance?.RegisterHarvest();
    }

    private void RegisterHarvest()
    {
        ComboState state = this.combo.Value;
        state.Count++;
        state.TimeoutClock.Restart();

        if (Milestones.Contains(state.Count))
        {
            state.StrongPop = true;
            state.AnimationClock.Restart();
        }
        else if (!state.StrongPop || state.AnimationClock.Elapsed.TotalSeconds >= ComboState.StrongPopDuration)
        {
            state.StrongPop = false;
            state.AnimationClock.Restart();
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        ComboState state = this.combo.Value;
        if (state.Count > 0 && state.TimeoutClock.Elapsed.TotalSeconds >= this.config.ComboTimeoutSeconds)
            state.Reset();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.combo.Value.Reset();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.combo.Value.Reset();
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        ComboState state = this.combo.Value;
        if (state.Count <= 0)
            return;

        double remainingSeconds = this.config.ComboTimeoutSeconds - state.TimeoutClock.Elapsed.TotalSeconds;
        if (remainingSeconds <= 0)
            return;

        float opacity = MathHelper.Clamp((float)(remainingSeconds / 0.25), 0f, 1f);
        float scale = this.GetDisplayScale(state);
        bool emphasize = state.StrongPop && state.AnimationClock.Elapsed.TotalSeconds < ComboState.StrongPopDuration;
        string text = this.Helper.Translation.Get("hud.combo", new { count = state.Count });

        Vector2 naturalSize = Game1.dialogueFont.MeasureString(text);
        Vector2 renderedSize = naturalSize * scale;
        const int horizontalPadding = 28;
        const int verticalPadding = 14;
        int panelWidth = (int)Math.Ceiling(renderedSize.X) + horizontalPadding * 2;
        int panelHeight = (int)Math.Ceiling(renderedSize.Y) + verticalPadding * 2;
        int panelX = (Game1.uiViewport.Width - panelWidth) / 2;
        const int panelY = 96;

        IClickableMenu.drawTextureBox(
            e.SpriteBatch,
            panelX,
            panelY,
            panelWidth,
            panelHeight,
            Color.White * (opacity * 0.92f)
        );

        Vector2 textPosition = new(
            panelX + (panelWidth - renderedSize.X) / 2f,
            panelY + (panelHeight - renderedSize.Y) / 2f - 2f
        );
        Color textColor = emphasize ? new Color(181, 110, 38) : Game1.textColor;
        Vector2 shadowOffset = new(2f, 2f);

        e.SpriteBatch.DrawString(
            Game1.dialogueFont,
            text,
            textPosition + shadowOffset,
            Color.Black * (opacity * 0.25f),
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            0.99f
        );
        e.SpriteBatch.DrawString(
            Game1.dialogueFont,
            text,
            textPosition,
            textColor * opacity,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            1f
        );
    }

    private float GetDisplayScale(ComboState state)
    {
        double duration = state.StrongPop ? ComboState.StrongPopDuration : ComboState.NormalPopDuration;
        double progress = Math.Min(state.AnimationClock.Elapsed.TotalSeconds / duration, 1.0);
        float strength = state.StrongPop ? 0.24f : 0.08f;
        float decay = (float)((1.0 - progress) * (1.0 - progress));
        return 1f + strength * decay;
    }

    private readonly record struct CropHarvestState(
        bool WasReady,
        int CurrentPhase,
        int DayOfCurrentPhase,
        bool WasFullyGrown
    );

    private sealed class ComboState
    {
        public const double NormalPopDuration = 0.16;
        public const double StrongPopDuration = 0.45;

        public int Count { get; set; }

        public bool StrongPop { get; set; }

        public Stopwatch TimeoutClock { get; } = new();

        public Stopwatch AnimationClock { get; } = new();

        public void Reset()
        {
            this.Count = 0;
            this.StrongPop = false;
            this.TimeoutClock.Reset();
            this.AnimationClock.Reset();
        }
    }
}
