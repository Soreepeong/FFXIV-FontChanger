using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using static FFXIVClientStructs.FFXIV.Client.UI.AddonNamePlate;

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
/// back from the alpha. The region is allocated S times larger (AllocateBake with TextW/TextH scaled), the text is baked
/// with the font set's node scale S instead of 1 (the per-node text setup), and the cached draw (DrawBaked) is given the
/// node's size and the text offset times S and its transform divided by S (exactly 1 when shown at S), so it draws the
/// region at the plate's size. A plate shown larger than its bake is baked again at that size.</para>
/// </remarks>
internal sealed unsafe class NamePlateText : IDisposable
{
    // The plate's distance factor never goes below this (OnRequestedUpdate clamps it).
    private const float MinDistanceFactor = 0.05f;

    // Bake scales beyond these are not worth it (or are a measuring error).
    private const float MinBakeScale = 0.5f;
    private const float MaxBakeScale = 4f;

    // AddonNamePlate.BakePlate's offset in the addon; from the field's attribute (see FieldOffsets).
    private static readonly nint AddonBakePlateOffset = FieldOffsets.Of(typeof(AddonNamePlate), nameof(AddonNamePlate.BakePlate));

    // A plate shown this much larger than its bake is baked again; within this of its bake scale it is drawn at exactly 1.
    private const float ScaleTolerance = 0.02f;

    private readonly FontReplacer replacer;
    private readonly IHostHook<AllocateBakeDelegate> allocateBakeHook;
    private readonly IHostHook<PrepareDelegate> prepareHook;
    private readonly IHostHook<DrawBakedDelegate> drawBakedHook;
    private readonly IHostHook<BakePlateDrawDelegate> bakePlateDrawHook;

    // The scale each object's current region was baked at, by the object (its BakeData, at its start).
    private readonly Dictionary<nint, float> bakeScales = [];

    // The renderer last seen by a hook, and whether every plate must be baked again once one is seen.
    private BakePlateRenderer* lastRenderer;
    private bool rebakePending;

    private NamePlateMode mode = NamePlateMode.BakedAtFullSize;

    public NamePlateText(FontReplacer replacer)
    {
        this.replacer = replacer;
        try
        {
            // AllocateBake is called by OnRequestedUpdate for the plates with NeedsToBeBaked, which it clears on success;
            // DrawBaked point samples only when the transform's scale is exactly 1.
            var host = Host.Current;
            var allocateBake = BakePlateRenderer.Addresses.AllocateBake.Value;
            var drawBaked = BakePlateRenderer.Addresses.DrawBaked.Value;

            // Draw and PrepareNode are virtual: the hooks go where the renderer's table points.
            var vtable = BakePlateRenderer.StaticVirtualTablePointer;
            if (vtable is null)
                throw new InvalidOperationException("The nameplate renderer's table isn't in this version of the game.");
            var prepare = (nint)vtable->PrepareNode;
            var bakePlateDraw = (nint)vtable->Draw;
            this.allocateBakeHook = host.Hook<AllocateBakeDelegate>(allocateBake, this.AllocateBakeDetour);
            this.prepareHook = host.Hook<PrepareDelegate>(prepare, this.PrepareDetour);
            this.drawBakedHook = host.Hook<DrawBakedDelegate>(drawBaked, this.DrawBakedDetour);
            this.bakePlateDrawHook = host.Hook<BakePlateDrawDelegate>(bakePlateDraw, this.BakePlateDrawDetour);
            this.allocateBakeHook.Enable();
            this.prepareHook.Enable();
            this.drawBakedHook.Enable();
            this.bakePlateDrawHook.Enable();
            this.replacer.TextInvalidated += this.ForceRebake;
            this.ForceRebake();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate byte AllocateBakeDelegate(BakePlateRenderer* renderer, NamePlateObject* obj);

    private delegate void PrepareDelegate(BakePlateRenderer* renderer, float* rect, FontSet* set, AtkResNode* node);

    private delegate void DrawBakedDelegate(BakePlateRenderer* renderer, AtkResNode* node, BakeData* bake);

    private delegate void BakePlateDrawDelegate(BakePlateRenderer* renderer, AtkResNode* node);

    /// <summary>Gets or sets how nameplate text is drawn while the replacement is on. Framework thread.</summary>
    public NamePlateMode Mode
    {
        get => this.mode;
        set
        {
            if (this.mode == value)
                return;
            this.mode = value;
            this.ForceRebake();
        }
    }

    private NamePlateMode EffectiveMode => this.replacer.Enabled ? this.mode : NamePlateMode.Game;

    public void Dispose()
    {
        if (this.replacer is not null)
            this.replacer.TextInvalidated -= this.ForceRebake;
        this.bakePlateDrawHook?.Dispose();
        this.allocateBakeHook?.Dispose();

        // Regions baked at another scale must not be drawn without the hooks that know their scale: a plate that needs
        // baking is drawn live (BakePlateRenderer.Draw) until its next update allocates and bakes it again.
        try
        {
            this.ForceRebake();
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Marking nameplates for baking failed");
        }

        this.prepareHook?.Dispose();
        this.drawBakedHook?.Dispose();
    }

    /// <summary>
    /// Makes every plate allocate and bake its region again at its next update. The plates are reached from the renderer
    /// the hooks see (it is embedded in the NamePlate addon): through the one seen last if that addon is still loaded, else
    /// at the next draw.
    /// </summary>
    private void ForceRebake()
    {
        if (this.lastRenderer is null)
        {
            this.rebakePending = true;
            return;
        }

        var addon = (AddonNamePlate*)((byte*)this.lastRenderer - AddonBakePlateOffset);
        if (!IsLoaded((nint)addon))
        {
            this.rebakePending = true;
            return;
        }

        this.rebakePending = false;
        var objects = addon->NamePlateObjectArray;
        if (objects is null)
            return;
        for (var i = 0; i < NumNamePlateObjects; i++)
            objects[i].NeedsToBeBaked = true;
    }

    // Whether a unit is still loaded: it is in the unit manager's list of loaded ones.
    private static bool IsLoaded(nint unit)
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is null)
            return false;
        ref var loaded = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            if ((nint)loaded.Entries[i].Value == unit)
                return true;
        }

        return false;
    }

    /// <summary>Remembers the renderer a hook was called with, and makes the bakes asked for before it was seen.</summary>
    private void SeeRenderer(BakePlateRenderer* renderer)
    {
        this.lastRenderer = renderer;
        if (this.rebakePending)
            this.ForceRebake();
    }

    /// <summary>Gets a node's transform (a 2x2 matrix of floats, row by row).</summary>
    private static ref float Transform(AtkResNode* node, int index) => ref ((float*)&node->Transform)[index];

    /// <summary>Gets the text node's on-screen scale as its nodes are now: the product of its and its ancestors' scales.</summary>
    private static float? GetShownScale(NamePlateObject* obj)
    {
        var text = (AtkResNode*)obj->NameText;
        if (text is null)
            return null;
        var scale = 1f;
        for (var node = text; node is not null; node = node->ParentNode)
            scale *= node->ScaleX;
        return float.IsFinite(scale) && scale > 0 ? scale : null;
    }

    /// <summary>
    /// Gets the text node's on-screen scale with the plate at 100 percent: its current scale divided by the plate's
    /// current distance factor, which OnRequestedUpdate stored as the bake alpha (factor^(1/4) * 255) this update.
    /// </summary>
    private static float? GetFullSizeScale(NamePlateObject* obj)
    {
        if (GetShownScale(obj) is not { } shown)
            return null;
        var a = obj->BakeData.Alpha / 255f;
        var factor = Math.Max(a * a * a * a, MinDistanceFactor);
        var scale = shown / factor;
        return float.IsFinite(scale) ? Math.Clamp(scale, MinBakeScale, MaxBakeScale) : null;
    }

    private byte AllocateBakeDetour(BakePlateRenderer* renderer, NamePlateObject* obj)
    {
        this.SeeRenderer(renderer);
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

    private void PrepareDetour(BakePlateRenderer* renderer, float* rect, FontSet* set, AtkResNode* node)
    {
        this.prepareHook.Original(renderer, rect, set, node);

        // A bake of a region allocated at a scale: lay the text out at that scale (the original sets 1).
        var bake = renderer->CurrentBakeData;
        if (bake is not null && this.bakeScales.TryGetValue((nint)bake, out var scale))
        {
            set->NodeScaleX = scale;
            set->NodeScaleY = scale;
        }
    }

    private void DrawBakedDetour(BakePlateRenderer* renderer, AtkResNode* node, BakeData* bake)
    {
        this.SeeRenderer(renderer);
        if (!this.bakeScales.TryGetValue((nint)bake, out var scale))
        {
            this.drawBakedHook.Original(renderer, node, bake);
            return;
        }

        var transform = stackalloc float[4];
        for (var i = 0; i < 4; i++)
            transform[i] = Transform(node, i);
        var shown = MathF.Sqrt((transform[0] * transform[0]) + (transform[1] * transform[1]));

        // Shown larger than it was baked for (a percent over 100 up close, a targeted plate): bake it again at that size
        // (BakeData is the object's first member). It is drawn live until then.
        var obj = (NamePlateObject*)bake;
        if (shown > scale * (1 + ScaleTolerance) && !obj->NeedsToBeBaked && this.EffectiveMode == NamePlateMode.BakedAtFullSize)
            obj->NeedsToBeBaked = true;

        // The region is in bake pixels, scale per node unit: give the original the node in bake pixels too (its size and
        // the text offset times the scale), so the region lands at the plate's size. Shown at (about) the scale it was
        // baked at, the transform is exactly 1: the original point samples only then, and bilinear sampling at a
        // fractional position (plates move smoothly) blends neighbouring texels even at 1:1. Plates aren't rotated.
        var (width, height) = (node->Width, node->Height);
        var textYOffset = bake->TextYOffset;
        var exact = MathF.Abs((shown / scale) - 1) < ScaleTolerance;
        for (var i = 0; i < 4; i++)
            Transform(node, i) = exact ? (i is 0 or 3 ? 1 : 0) : transform[i] / scale;

        node->Width = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(width * scale));
        node->Height = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(height * scale));
        bake->TextYOffset = (short)Rounding.Round(textYOffset * scale);
        try
        {
            this.drawBakedHook.Original(renderer, node, bake);
        }
        finally
        {
            for (var i = 0; i < 4; i++)
                Transform(node, i) = transform[i];
            (node->Width, node->Height) = (width, height);
            bake->TextYOffset = textYOffset;
        }
    }

    /// <summary>In <see cref="NamePlateMode.Live"/>, the renderer's own live mode (set by the game's text render mode setting).</summary>
    private void BakePlateDrawDetour(BakePlateRenderer* renderer, AtkResNode* node)
    {
        this.SeeRenderer(renderer);
        if (this.EffectiveMode != NamePlateMode.Live)
        {
            this.bakePlateDrawHook.Original(renderer, node);
            return;
        }

        var liveMode = &renderer->DisableFixedFontResolution;
        var saved = *liveMode;
        *liveMode = 1;
        try
        {
            this.bakePlateDrawHook.Original(renderer, node);
        }
        finally
        {
            *liveMode = saved;
        }
    }
}
