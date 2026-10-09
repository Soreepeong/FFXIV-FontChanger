using System;
using System.Collections.Generic;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts;

/// <summary>How nameplate text is drawn.</summary>
internal enum NamePlateMode
{
    /// <summary>As the game does: baked once at the text node's unscaled size, drawn scaled (soft as plates zoom).</summary>
    Game,

    /// <summary>Baked once at the size the plate has up close, drawn scaled down from there: 1:1 at full size.</summary>
    BakedAtFullSize,

    /// <summary>Laid out and drawn every frame at the size shown: always sharp, but costly with many plates.</summary>
    Live,
}

/// <summary>
/// Nameplate text with the replaced fonts.
/// </summary>
/// <remarks>
/// <para>The game bakes each plate's name into one shared render texture (AddonNamePlate::BakePlateRenderer), once per
/// text change, and draws that region every frame under the plate's transform. OnRequestedUpdate scales each plate's
/// root component to <c>max(percent / 100, 0.05) * size</c> (SetCommonNamePlate's body, inlined in 7.56h; the function
/// itself has no callers) and stores <c>(percent / 100)^(1/4) * 255</c> as the bake alpha. The bake is made at the text
/// node's unscaled size, so the plate is resampled at every distance.</para>
/// <para>Baking at full size: the bake scale S is the text node's on-screen scale at 100 percent: its current scale (the
/// product of its and its ancestors' scales) divided by the current <c>max(percent / 100, 0.05)</c>, the percent taken
/// back from the alpha. The region is allocated S times larger (FUN_141326FF0 with TextW/TextH scaled), the text is baked
/// with the font set's node scale S instead of 1 (BakePlateRenderer vf3), and the cached draw (FUN_141327640) is given the
/// node's size and the text offset times S and its transform divided by S (exactly 1 when shown at S), so it draws the
/// region at the plate's size. A plate shown larger than its bake is baked again at that size.</para>
/// </remarks>
internal sealed unsafe class NamePlateText : IDisposable
{
    // bool AllocateBake(BakePlateRenderer* this, NamePlateObject* obj) (FUN_141326FF0): frees the object's region and
    // takes a new one of ((TextW + 8) * 2) x ((TextH + 4) * 2) in the render texture; false when it doesn't fit. Called by
    // OnRequestedUpdate for the plates with NeedsToBeBaked, which it clears on success. Unique.
    private const string AllocateBakeSignature =
        "48 89 5C 24 18 48 89 6C 24 20 57 41 54 41 55 41 56 41 57 48 83 EC 50 4C 8B 81 F8 01 00 00 4C 8B F2 4C 8B 4A 20 4C 8B E9";

    // void BakePlateRenderer.vf3(BakePlateRenderer* this, float* rect, GameFontSet* set, AtkResNode* node): the text node
    // renderer's per-node setup, then for a bake (CurrentBakeData set) the bake rect and node scale 1. Unique.
    private const string PrepareSignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 49 8B F0 48 8B DA 48 8B F9 E8 ?? ?? ?? ?? 48 83 BF 28 02 00 00 00 74 6C 48";

    // void DrawBaked(BakePlateRenderer* this, AtkResNode* node, BakeData* bake) (FUN_141327640): draws a baked region,
    // (Width - 4) x (Height - 4) node units at the node's transform, centered on the node; point sampled only when the
    // transform's scale is exactly 1. Unique.
    private const string DrawBakedSignature =
        "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 54 41 56 41 57 48 83 EC 50 F3 0F 10 42 64 4D 8B F0 F3 0F 10";

    // void BakePlateRenderer.Draw(BakePlateRenderer* this, AtkResNode* node) (0x141326C70, vtable 0x142247E08 slot 2).
    // A twin (FUN_1413B7910) differs from the "80 78 71" on (its bake entry fields are elsewhere).
    private const string BakePlateDrawSignature =
        "48 89 5C 24 10 57 48 83 EC 20 48 8B F9 48 8B DA 48 8B 89 F8 01 00 00 4C 8B C1 48 8B 41 08 80 78 19 00 75 18 " +
        "48 39 58 20 73 06 48 8B 40 10 EB 06 4C 8B C0 48 8B 00 80 78 19 00 74 E8 41 80 78 19 00 75 06 49 3B 58 20 73 03 " +
        "4C 8B C1 80 BF 30 02 00 00 00 75 7E 4C 3B C1 74 79 49 8B 40 28 80 78 71";

    private const int NamePlateObjectCount = 50;

    // The plate's distance factor never goes below this (OnRequestedUpdate clamps it).
    private const float MinDistanceFactor = 0.05f;

    // Bake scales beyond these are not worth it (or are a measuring error).
    private const float MinBakeScale = 0.5f;
    private const float MaxBakeScale = 4f;

    // A plate shown this much larger than its bake is baked again; within this of its bake scale it is drawn at exactly 1.
    private const float ScaleTolerance = 0.02f;

    private readonly FontReplacer replacer;
    private readonly Hook<AllocateBakeDelegate> allocateBakeHook;
    private readonly Hook<PrepareDelegate> prepareHook;
    private readonly Hook<DrawBakedDelegate> drawBakedHook;
    private readonly Hook<BakePlateDrawDelegate> bakePlateDrawHook;

    // The scale each object's current region was baked at.
    private readonly Dictionary<nint, float> bakeScales = [];

    private NamePlateMode mode = NamePlateMode.BakedAtFullSize;

    public NamePlateText(FontReplacer replacer)
    {
        this.replacer = replacer;
        try
        {
            var interop = Plugin.GameInterop;
            this.allocateBakeHook = interop.HookFromSignature<AllocateBakeDelegate>(AllocateBakeSignature, this.AllocateBakeDetour);
            this.prepareHook = interop.HookFromSignature<PrepareDelegate>(PrepareSignature, this.PrepareDetour);
            this.drawBakedHook = interop.HookFromSignature<DrawBakedDelegate>(DrawBakedSignature, this.DrawBakedDetour);
            this.bakePlateDrawHook = interop.HookFromSignature<BakePlateDrawDelegate>(BakePlateDrawSignature, this.BakePlateDrawDetour);
            this.allocateBakeHook.Enable();
            this.prepareHook.Enable();
            this.drawBakedHook.Enable();
            this.bakePlateDrawHook.Enable();
            this.replacer.TextInvalidated += ForceRebake;
            ForceRebake();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate byte AllocateBakeDelegate(nint renderer, AddonNamePlate.NamePlateObject* obj);

    private delegate void PrepareDelegate(nint renderer, float* rect, GameFontSet* set, AtkResNode* node);

    private delegate void DrawBakedDelegate(nint renderer, AtkResNode* node, AddonNamePlate.BakeData* bake);

    private delegate void BakePlateDrawDelegate(nint renderer, AtkResNode* node);

    /// <summary>Gets or sets how nameplate text is drawn while the replacement is on. Framework thread.</summary>
    public NamePlateMode Mode
    {
        get => this.mode;
        set
        {
            if (this.mode == value)
                return;
            this.mode = value;
            ForceRebake();
        }
    }

    private NamePlateMode EffectiveMode => this.replacer.Enabled ? this.mode : NamePlateMode.Game;

    public void Dispose()
    {
        if (this.replacer is not null)
            this.replacer.TextInvalidated -= ForceRebake;
        this.bakePlateDrawHook?.Dispose();
        this.allocateBakeHook?.Dispose();

        // Regions baked at another scale must not be drawn without the hooks that know their scale: a plate that needs
        // baking is drawn live (BakePlateRenderer.Draw) until its next update allocates and bakes it again.
        try
        {
            ForceRebake();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Marking nameplates for baking failed");
        }

        this.prepareHook?.Dispose();
        this.drawBakedHook?.Dispose();
    }

    /// <summary>Makes every plate allocate and bake its region again at its next update.</summary>
    private static void ForceRebake()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
            return;
        var addon = (AddonNamePlate*)manager->GetAddonByName("NamePlate");
        if (addon is null || addon->NamePlateObjectArray is null)
            return;
        for (var i = 0; i < NamePlateObjectCount; i++)
            addon->NamePlateObjectArray[i].NeedsToBeBaked = true;
    }

    /// <summary>Gets the text node's on-screen scale as its nodes are now: the product of its and its ancestors' scales.</summary>
    private static float? GetShownScale(AddonNamePlate.NamePlateObject* obj)
    {
        if (obj->NameText is null)
            return null;
        var scale = 1f;
        for (var node = (AtkResNode*)obj->NameText; node is not null; node = node->ParentNode)
            scale *= node->ScaleX;
        return float.IsFinite(scale) && scale > 0 ? scale : null;
    }

    /// <summary>
    /// Gets the text node's on-screen scale with the plate at 100 percent: its current scale divided by the plate's
    /// current distance factor, which OnRequestedUpdate stored as the bake alpha (factor^(1/4) * 255) this update.
    /// </summary>
    private static float? GetFullSizeScale(AddonNamePlate.NamePlateObject* obj)
    {
        if (GetShownScale(obj) is not { } shown)
            return null;
        var a = obj->BakeData.Alpha / 255f;
        var factor = Math.Max(a * a * a * a, MinDistanceFactor);
        var scale = shown / factor;
        return float.IsFinite(scale) ? Math.Clamp(scale, MinBakeScale, MaxBakeScale) : null;
    }

    private byte AllocateBakeDetour(nint renderer, AddonNamePlate.NamePlateObject* obj)
    {
        var previous = this.bakeScales.GetValueOrDefault((nint)obj);
        this.bakeScales.Remove((nint)obj);
        if (this.EffectiveMode != NamePlateMode.BakedAtFullSize || GetFullSizeScale(obj) is not { } fullSize)
            return this.allocateBakeHook.Original(renderer, obj);

        // At least the size it is shown at now, and what it was baked at before (it was shown that large).
        var scale = Math.Clamp(Math.Max(fullSize, Math.Max(GetShownScale(obj) ?? 0, previous)), MinBakeScale, MaxBakeScale);

        var (w, h) = (obj->TextW, obj->TextH);
        obj->TextW = (short)MathF.Ceiling(w * scale);
        obj->TextH = (short)MathF.Ceiling(h * scale);
        var allocated = this.allocateBakeHook.Original(renderer, obj);
        (obj->TextW, obj->TextH) = (w, h);
        if (allocated != 0)
            this.bakeScales[(nint)obj] = scale;

        return allocated;
    }

    private void PrepareDetour(nint renderer, float* rect, GameFontSet* set, AtkResNode* node)
    {
        this.prepareHook.Original(renderer, rect, set, node);

        // A bake of a region allocated at a scale: lay the text out at that scale (the original sets 1).
        var bake = ((AddonNamePlate.BakePlateRenderer*)renderer)->CurrentBakeData;
        if (bake is not null && this.bakeScales.TryGetValue((nint)bake, out var scale))
        {
            set->NodeScaleX = scale;
            set->NodeScaleY = scale;
        }
    }

    private void DrawBakedDetour(nint renderer, AtkResNode* node, AddonNamePlate.BakeData* bake)
    {
        if (!this.bakeScales.TryGetValue((nint)bake, out var scale))
        {
            this.drawBakedHook.Original(renderer, node, bake);
            return;
        }

        var transform = node->Transform;
        var shown = MathF.Sqrt((transform.M11 * transform.M11) + (transform.M12 * transform.M12));

        // Shown larger than it was baked for (a percent over 100 up close, a targeted plate): bake it again at that size
        // (BakeData is the object's first member). It is drawn live until then.
        var obj = (AddonNamePlate.NamePlateObject*)bake;
        if (shown > scale * (1 + ScaleTolerance) && !obj->NeedsToBeBaked && this.EffectiveMode == NamePlateMode.BakedAtFullSize)
            obj->NeedsToBeBaked = true;

        // The region is in bake pixels, scale per node unit: give the original the node in bake pixels too (its size and
        // the text offset times the scale), so the region lands at the plate's size. Shown at (about) the scale it was
        // baked at, the transform is exactly 1: the original point samples only then, and bilinear sampling at a
        // fractional position (plates move smoothly) blends neighbouring texels even at 1:1. Plates aren't rotated.
        var (width, height) = (node->Width, node->Height);
        var textYOffset = bake->TextYOffset;
        if (MathF.Abs((shown / scale) - 1) < ScaleTolerance)
        {
            node->Transform.M11 = 1;
            node->Transform.M12 = 0;
            node->Transform.M21 = 0;
            node->Transform.M22 = 1;
        }
        else
        {
            node->Transform.M11 /= scale;
            node->Transform.M12 /= scale;
            node->Transform.M21 /= scale;
            node->Transform.M22 /= scale;
        }

        node->Width = (ushort)Math.Min(ushort.MaxValue, MathF.Round(width * scale));
        node->Height = (ushort)Math.Min(ushort.MaxValue, MathF.Round(height * scale));
        bake->TextYOffset = (short)MathF.Round(textYOffset * scale);
        try
        {
            this.drawBakedHook.Original(renderer, node, bake);
        }
        finally
        {
            node->Transform = transform;
            (node->Width, node->Height) = (width, height);
            bake->TextYOffset = textYOffset;
        }
    }

    /// <summary>In <see cref="NamePlateMode.Live"/>, the renderer's own live mode (+0x230, set by the game's text render mode setting).</summary>
    private void BakePlateDrawDetour(nint renderer, AtkResNode* node)
    {
        if (this.EffectiveMode != NamePlateMode.Live)
        {
            this.bakePlateDrawHook.Original(renderer, node);
            return;
        }

        var p = (AddonNamePlate.BakePlateRenderer*)renderer;
        var saved = p->DisableFixedFontResolution;
        p->DisableFixedFontResolution = 1;
        try
        {
            this.bakePlateDrawHook.Original(renderer, node);
        }
        finally
        {
            p->DisableFixedFontResolution = saved;
        }
    }
}
