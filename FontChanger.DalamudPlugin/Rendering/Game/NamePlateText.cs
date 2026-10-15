using System;
using System.Collections.Generic;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using static FFXIVClientStructs.FFXIV.Client.UI.AddonNamePlate;

namespace FontChanger.DalamudPlugin.Rendering.Game;

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
    private static readonly nint AddonBakePlateOffset = typeof(AddonNamePlate).FieldOffsetOf(nameof(AddonNamePlate.BakePlate));

    // A plate shown this much larger than its bake is baked again; within this of its bake scale it is drawn at exactly 1.
    private const float ScaleTolerance = 0.02f;

    private readonly FontReplacer _replacer;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();
    private readonly Hook<AllocateBakeDelegate> _allocateBakeHook;
    private readonly Hook<PrepareDelegate> _prepareHook;
    private readonly Hook<DrawBakedDelegate> _drawBakedHook;
    private readonly Hook<BakePlateDrawDelegate> _bakePlateDrawHook;

    // The scale each object's current region was baked at, by the object's BakeData (at its start).
    private readonly Dictionary<Pointer<BakeData>, float> _bakeScales = [];

    // The renderer last seen by a hook, and whether every plate must be baked again once one is seen.
    private BakePlateRenderer* _lastRenderer;
    private bool _rebakePending;

    private NamePlateMode _mode = NamePlateMode.BakedAtFullSize;

    public NamePlateText(FontReplacer replacer)
    {
        this._replacer = replacer;
        try
        {
            // AllocateBake is called by OnRequestedUpdate for the plates with NeedsToBeBaked, which it clears on success;
            // DrawBaked point samples only when the transform's scale is exactly 1.
            var allocateBake = BakePlateRenderer.Addresses.AllocateBake.Value;
            var drawBaked = BakePlateRenderer.Addresses.DrawBaked.Value;

            // Draw and PrepareNode are virtual: the hooks go where the renderer's table points.
            var vtable = BakePlateRenderer.StaticVirtualTablePointer;
            if (vtable is null)
                throw new InvalidOperationException("The nameplate renderer's table isn't in this version of the game.");
            var prepare = (nint)vtable->PrepareNode;
            var bakePlateDraw = (nint)vtable->Draw;

            // Registered so that they are undone in this order: Draw and AllocateBake, then the rebake, then the hooks that
            // know the scale of regions baked before.
            this._drawBakedHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<DrawBakedDelegate>(drawBaked, this.DrawBakedDetour));
            this._prepareHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<PrepareDelegate>(prepare, this.PrepareDetour));

            // Regions baked at another scale must not be drawn without the hooks that know their scale: a plate that needs
            // baking is drawn live (BakePlateRenderer.Draw) until its next update allocates and bakes it again.
            this._finalizer.Add(() => {
                try
                {
                    this.ForceRebake();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Marking nameplates for baking failed");
                }
            });
            this._allocateBakeHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<AllocateBakeDelegate>(allocateBake, this.AllocateBakeDetour));
            this._bakePlateDrawHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<BakePlateDrawDelegate>(bakePlateDraw, this.BakePlateDrawDetour));
            this._allocateBakeHook.Enable();
            this._prepareHook.Enable();
            this._drawBakedHook.Enable();
            this._bakePlateDrawHook.Enable();
            this._replacer.TextInvalidated += this.ForceRebake;
            this._finalizer.Add(() => this._replacer.TextInvalidated -= this.ForceRebake);
            this.ForceRebake();
        }
        catch
        {
            try
            {
                this._finalizer.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Undoing a failed nameplate text setup failed");
            }

            throw;
        }
    }

    private delegate byte AllocateBakeDelegate(BakePlateRenderer* renderer, NamePlateObject* obj);

    private delegate void PrepareDelegate(BakePlateRenderer* renderer, float* rect, AtkFontManager.FontSet* set, AtkTextNode* node);

    private delegate void DrawBakedDelegate(BakePlateRenderer* renderer, AtkResNode* node, BakeData* bake);

    private delegate void BakePlateDrawDelegate(BakePlateRenderer* renderer, AtkTextNode* node);

    /// <summary>Gets or sets how nameplate text is drawn while the replacement is on. Framework thread.</summary>
    public NamePlateMode Mode
    {
        get
        {
            Misc.DebugAssertMainThread();
            return this._mode;
        }
        set
        {
            Misc.DebugAssertMainThread();
            if (this._mode == value)
                return;
            this._mode = value;
            this.ForceRebake();
        }
    }

    private NamePlateMode EffectiveMode => this._replacer.Enabled ? this._mode : NamePlateMode.Game;

    /// <summary>Undoes everything; a step that fails is collected, and the others still run. Framework thread.</summary>
    public void Dispose()
    {
        Misc.DebugAssertMainThread();
        this._finalizer.Dispose();
    }

    /// <summary>
    /// Makes every plate allocate and bake its region again at its next update. The plates are reached from the renderer
    /// the hooks see (it is embedded in the NamePlate addon): through the one seen last if that addon is still loaded, else
    /// at the next draw.
    /// </summary>
    private void ForceRebake()
    {
        if (this._lastRenderer is null)
        {
            this._rebakePending = true;
            return;
        }

        var addon = (AddonNamePlate*)((byte*)this._lastRenderer - AddonBakePlateOffset);
        if (!IsLoaded(&addon->AtkUnitBase))
        {
            this._rebakePending = true;
            return;
        }

        this._rebakePending = false;
        var objects = addon->NamePlateObjectArray;
        if (objects is null)
            return;
        for (var i = 0; i < NumNamePlateObjects; i++)
            objects[i].NeedsToBeBaked = true;
    }

    private static bool IsLoaded(AtkUnitBase* unit)
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is null)
            return false;
        ref var loaded = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            if (loaded.Entries[i].Value == unit)
                return true;
        }

        return false;
    }

    /// <summary>Remembers the renderer a hook was called with, and makes the bakes asked for before it was seen.</summary>
    private void SeeRenderer(BakePlateRenderer* renderer)
    {
        this._lastRenderer = renderer;
        if (this._rebakePending)
            this.ForceRebake();
    }

    /// <summary>Gets a node's transform (a 2x2 matrix of floats, row by row).</summary>
    private static ref float Transform(AtkResNode* node, int index) => ref ((float*)&node->Transform)[index];

    /// <summary>Gets the text node's on-screen scale as its nodes are now: the product of its and its ancestors' scales.</summary>
    private static bool TryGetShownScale(NamePlateObject* obj, out float scale)
    {
        scale = 0;
        var text = (AtkResNode*)obj->NameText;
        if (text is null)
            return false;
        var product = 1f;
        for (var node = text; node is not null; node = node->ParentNode)
            product *= node->ScaleX;
        if (!float.IsFinite(product) || product <= 0)
            return false;
        scale = product;
        return true;
    }

    private static float GetShownScaleOrDefault(NamePlateObject* obj, float fallback) => TryGetShownScale(obj, out var scale) ? scale : fallback;

    /// <summary>
    /// Gets the text node's on-screen scale with the plate at 100 percent: its current scale divided by the plate's
    /// current distance factor, which OnRequestedUpdate stored as the bake alpha (factor^(1/4) * 255) this update.
    /// </summary>
    private static bool TryGetFullSizeScale(NamePlateObject* obj, out float fullSize)
    {
        fullSize = 0;
        if (!TryGetShownScale(obj, out var shown))
            return false;
        var a = obj->BakeData.Alpha / 255f;
        var factor = Math.Max(a * a * a * a, MinDistanceFactor);
        var scale = shown / factor;
        if (!float.IsFinite(scale))
            return false;
        fullSize = Math.Clamp(scale, MinBakeScale, MaxBakeScale);
        return true;
    }

    private byte AllocateBakeDetour(BakePlateRenderer* renderer, NamePlateObject* obj)
    {
        this.SeeRenderer(renderer);
        Pointer<BakeData> bake = &obj->BakeData;
        var previous = this._bakeScales.GetValueOrDefault(bake);
        this._bakeScales.Remove(bake);
        if (this.EffectiveMode != NamePlateMode.BakedAtFullSize || !TryGetFullSizeScale(obj, out var fullSize))
            return this._allocateBakeHook.Original(renderer, obj);

        // At least the size it is shown at now, and what it was baked at before (it was shown that large).
        var scale = Math.Clamp(Math.Max(fullSize, Math.Max(GetShownScaleOrDefault(obj, 0), previous)), MinBakeScale, MaxBakeScale);

        var (w, h) = (obj->TextW, obj->TextH);
        obj->TextW = (short)MathF.Ceiling(w * scale);
        obj->TextH = (short)MathF.Ceiling(h * scale);
        var allocated = this._allocateBakeHook.Original(renderer, obj);
        (obj->TextW, obj->TextH) = (w, h);
        if (allocated != 0)
            this._bakeScales[bake] = scale;

        return allocated;
    }

    private void PrepareDetour(BakePlateRenderer* renderer, float* rect, AtkFontManager.FontSet* set, AtkTextNode* node)
    {
        this._prepareHook.Original(renderer, rect, set, node);

        // A bake of a region allocated at a scale: lay the text out at that scale (the original sets 1).
        var bake = renderer->CurrentBakeData;
        if (bake is not null && this._bakeScales.TryGetValue(bake, out var scale))
        {
            set->NodeScaleX = scale;
            set->NodeScaleY = scale;
        }
    }

    private void DrawBakedDetour(BakePlateRenderer* renderer, AtkResNode* node, BakeData* bake)
    {
        this.SeeRenderer(renderer);
        if (!this._bakeScales.TryGetValue(bake, out var scale))
        {
            this._drawBakedHook.Original(renderer, node, bake);
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
            Transform(node, i) = exact ? i is 0 or 3 ? 1 : 0 : transform[i] / scale;

        node->Width = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(width * scale));
        node->Height = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(height * scale));
        bake->TextYOffset = (short)Rounding.Round(textYOffset * scale);
        try
        {
            this._drawBakedHook.Original(renderer, node, bake);
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
    private void BakePlateDrawDetour(BakePlateRenderer* renderer, AtkTextNode* node)
    {
        this.SeeRenderer(renderer);
        if (this.EffectiveMode != NamePlateMode.Live)
        {
            this._bakePlateDrawHook.Original(renderer, node);
            return;
        }

        var liveMode = &renderer->DisableFixedFontResolution;
        var saved = *liveMode;
        *liveMode = 1;
        try
        {
            this._bakePlateDrawHook.Original(renderer, node);
        }
        finally
        {
            *liveMode = saved;
        }
    }
}
