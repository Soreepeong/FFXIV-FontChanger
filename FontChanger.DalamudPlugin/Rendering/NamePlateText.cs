using System;
using System.Collections.Generic;

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
    // The plate's distance factor never goes below this (OnRequestedUpdate clamps it).
    private const float MinDistanceFactor = 0.05f;

    // Bake scales beyond these are not worth it (or are a measuring error).
    private const float MinBakeScale = 0.5f;
    private const float MaxBakeScale = 4f;

    // A plate shown this much larger than its bake is baked again; within this of its bake scale it is drawn at exactly 1.
    private const float ScaleTolerance = 0.02f;

    private readonly FontReplacer replacer;
    private readonly IHostHook<AllocateBakeDelegate> allocateBakeHook;
    private readonly IHostHook<PrepareDelegate> prepareHook;
    private readonly IHostHook<DrawBakedDelegate> drawBakedHook;
    private readonly IHostHook<BakePlateDrawDelegate> bakePlateDrawHook;

    // The scale each object's current region was baked at.
    private readonly Dictionary<nint, float> bakeScales = [];

    // AddonNamePlate: its BakePlateRenderer (embedded), and its NamePlateObjects (an array of NamePlateObjectArraySize
    // bytes, NamePlateObjectSize each).
    private int addonBakePlate;
    private int addonObjects;
    private int objectSize;
    private int objectCount;

    // NamePlateObject: its BakeData (at 0, so a BakeData* is its object's), text node, text size, and whether it needs a
    // bake. BakeData: the text's vertical offset and the bake alpha.
    private int objectNameText;
    private int objectTextW;
    private int objectTextH;
    private int objectNeedsToBeBaked;
    private int bakeTextYOffset;
    private int bakeAlpha;

    // BakePlateRenderer: the BakeData being baked, and its own live mode (set by the game's text render mode setting).
    private int rendererCurrentBakeData;
    private int rendererLiveMode;

    // AtkResNode: its transform (a 2x2 matrix of floats, row by row), size, parent and scale.
    private int nodeTransform;
    private int nodeWidth;
    private int nodeHeight;
    private int nodeParent;
    private int nodeScaleX;

    // The renderer last seen by a hook, and whether every plate must be baked again once one is seen.
    private nint lastRenderer;
    private bool rebakePending;

    private NamePlateMode mode = NamePlateMode.BakedAtFullSize;

    public NamePlateText(FontReplacer replacer)
    {
        this.replacer = replacer;
        try
        {
            nint allocateBake = 0, prepare = 0, drawBaked = 0, bakePlateDraw = 0;
            GameLayout.Resolve("Nameplate text", () =>
            {
                GameUi.ResolveUnits();

                // bool AllocateBake(BakePlateRenderer* this, NamePlateObject* obj): frees the object's region and takes a
                // new one of ((TextW + 8) * 2) x ((TextH + 4) * 2) in the render texture; false when it doesn't fit. Called
                // by OnRequestedUpdate for the plates with NeedsToBeBaked, which it clears on success.
                allocateBake = GameLayout.Address("NamePlateAllocateBake");

                // void BakePlateRenderer.vf3(BakePlateRenderer* this, float* rect, GameFontSet* set, AtkResNode* node): the
                // text node renderer's per-node setup, then for a bake (CurrentBakeData set) the bake rect and node scale 1.
                prepare = GameLayout.Address("NamePlateBakePrepare");

                // void DrawBaked(BakePlateRenderer* this, AtkResNode* node, BakeData* bake): draws a baked region, (Width -
                // 4) x (Height - 4) node units at the node's transform, centered on the node; point sampled only when the
                // transform's scale is exactly 1.
                drawBaked = GameLayout.Address("NamePlateDrawBaked");

                // void BakePlateRenderer.vf2(BakePlateRenderer* this, AtkResNode* node): draws a plate's text, baked or live.
                bakePlateDraw = GameLayout.Address("NamePlateBakePlateDraw");

                this.addonBakePlate = GameLayout.Get("AddonNamePlate.BakePlate");
                this.addonObjects = GameLayout.Get("AddonNamePlate.NamePlateObjectArray");
                this.objectSize = GameLayout.Get("NamePlateObject");
                this.objectCount = GameLayout.Get("NamePlateObjectArray") / Math.Max(this.objectSize, 1);
                this.objectNameText = GameLayout.Get("NamePlateObject.NameText");
                this.objectTextW = GameLayout.Get("NamePlateObject.TextW");
                this.objectTextH = GameLayout.Get("NamePlateObject.TextH");
                this.objectNeedsToBeBaked = GameLayout.Get("NamePlateObject.NeedsToBeBaked");
                this.bakeTextYOffset = GameLayout.Get("BakeData.TextYOffset");
                this.bakeAlpha = GameLayout.Get("BakeData.Alpha");
                this.rendererCurrentBakeData = GameLayout.Get("BakePlateRenderer.CurrentBakeData");
                this.rendererLiveMode = GameLayout.Get("BakePlateRenderer.DisableFixedFontResolution");

                // The transform's first row is captured as its two floats; the matrix starts at the first.
                this.nodeTransform = Math.Min(GameLayout.Get("AtkResNode.Transform.Row1A"), GameLayout.Get("AtkResNode.Transform.Row1B"));
                this.nodeWidth = GameLayout.Get("AtkResNode.Width");
                this.nodeHeight = GameLayout.Get("AtkResNode.Height");
                this.nodeParent = GameLayout.Get("AtkResNode.ParentNode");
                this.nodeScaleX = GameLayout.Get("AtkResNode.ScaleX");
                GameFontSet.Resolve();
            });

            var host = Host.Current;
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

    private delegate byte AllocateBakeDelegate(nint renderer, nint obj);

    private delegate void PrepareDelegate(nint renderer, float* rect, GameFontSet* set, nint node);

    private delegate void DrawBakedDelegate(nint renderer, nint node, nint bake);

    private delegate void BakePlateDrawDelegate(nint renderer, nint node);

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
        var addon = this.lastRenderer - this.addonBakePlate;
        if (this.lastRenderer == 0 || !GameUi.GetLoadedUnits().Contains(addon))
        {
            this.rebakePending = true;
            return;
        }

        this.rebakePending = false;
        var objects = *(nint*)(addon + this.addonObjects);
        if (objects == 0)
            return;
        for (var i = 0; i < this.objectCount; i++)
            this.SetNeedsToBeBaked(objects + (i * this.objectSize));
    }

    /// <summary>Remembers the renderer a hook was called with, and makes the bakes asked for before it was seen.</summary>
    private void SeeRenderer(nint renderer)
    {
        this.lastRenderer = renderer;
        if (this.rebakePending)
            this.ForceRebake();
    }

    private bool NeedsToBeBaked(nint obj) => *(byte*)(obj + this.objectNeedsToBeBaked) != 0;

    private void SetNeedsToBeBaked(nint obj) => *(byte*)(obj + this.objectNeedsToBeBaked) = 1;

    private ref float Transform(nint node, int index) => ref ((float*)(node + this.nodeTransform))[index];

    /// <summary>Gets the text node's on-screen scale as its nodes are now: the product of its and its ancestors' scales.</summary>
    private float? GetShownScale(nint obj)
    {
        var text = *(nint*)(obj + this.objectNameText);
        if (text == 0)
            return null;
        var scale = 1f;
        for (var node = text; node != 0; node = *(nint*)(node + this.nodeParent))
            scale *= *(float*)(node + this.nodeScaleX);
        return float.IsFinite(scale) && scale > 0 ? scale : null;
    }

    /// <summary>
    /// Gets the text node's on-screen scale with the plate at 100 percent: its current scale divided by the plate's
    /// current distance factor, which OnRequestedUpdate stored as the bake alpha (factor^(1/4) * 255) this update.
    /// </summary>
    private float? GetFullSizeScale(nint obj)
    {
        if (this.GetShownScale(obj) is not { } shown)
            return null;
        var a = *(byte*)(obj + this.bakeAlpha) / 255f;
        var factor = Math.Max(a * a * a * a, MinDistanceFactor);
        var scale = shown / factor;
        return float.IsFinite(scale) ? Math.Clamp(scale, MinBakeScale, MaxBakeScale) : null;
    }

    private byte AllocateBakeDetour(nint renderer, nint obj)
    {
        this.SeeRenderer(renderer);
        var previous = this.bakeScales.GetValueOrDefault(obj);
        this.bakeScales.Remove(obj);
        if (this.EffectiveMode != NamePlateMode.BakedAtFullSize || this.GetFullSizeScale(obj) is not { } fullSize)
            return this.allocateBakeHook.Original(renderer, obj);

        // At least the size it is shown at now, and what it was baked at before (it was shown that large).
        var scale = Math.Clamp(Math.Max(fullSize, Math.Max(this.GetShownScale(obj) ?? 0, previous)), MinBakeScale, MaxBakeScale);

        var textW = (short*)(obj + this.objectTextW);
        var textH = (short*)(obj + this.objectTextH);
        var (w, h) = (*textW, *textH);
        *textW = (short)MathF.Ceiling(w * scale);
        *textH = (short)MathF.Ceiling(h * scale);
        var allocated = this.allocateBakeHook.Original(renderer, obj);
        (*textW, *textH) = (w, h);
        if (allocated != 0)
            this.bakeScales[obj] = scale;

        return allocated;
    }

    private void PrepareDetour(nint renderer, float* rect, GameFontSet* set, nint node)
    {
        this.prepareHook.Original(renderer, rect, set, node);

        // A bake of a region allocated at a scale: lay the text out at that scale (the original sets 1).
        var bake = *(nint*)(renderer + this.rendererCurrentBakeData);
        if (bake != 0 && this.bakeScales.TryGetValue(bake, out var scale))
        {
            set->NodeScaleX = scale;
            set->NodeScaleY = scale;
        }
    }

    private void DrawBakedDetour(nint renderer, nint node, nint bake)
    {
        this.SeeRenderer(renderer);
        if (!this.bakeScales.TryGetValue(bake, out var scale))
        {
            this.drawBakedHook.Original(renderer, node, bake);
            return;
        }

        var transform = stackalloc float[4];
        for (var i = 0; i < 4; i++)
            transform[i] = this.Transform(node, i);
        var shown = MathF.Sqrt((transform[0] * transform[0]) + (transform[1] * transform[1]));

        // Shown larger than it was baked for (a percent over 100 up close, a targeted plate): bake it again at that size
        // (BakeData is the object's first member). It is drawn live until then.
        var obj = bake;
        if (shown > scale * (1 + ScaleTolerance) && !this.NeedsToBeBaked(obj) && this.EffectiveMode == NamePlateMode.BakedAtFullSize)
            this.SetNeedsToBeBaked(obj);

        // The region is in bake pixels, scale per node unit: give the original the node in bake pixels too (its size and
        // the text offset times the scale), so the region lands at the plate's size. Shown at (about) the scale it was
        // baked at, the transform is exactly 1: the original point samples only then, and bilinear sampling at a
        // fractional position (plates move smoothly) blends neighbouring texels even at 1:1. Plates aren't rotated.
        var widthField = (ushort*)(node + this.nodeWidth);
        var heightField = (ushort*)(node + this.nodeHeight);
        var textYOffsetField = (short*)(bake + this.bakeTextYOffset);
        var (width, height) = (*widthField, *heightField);
        var textYOffset = *textYOffsetField;
        var exact = MathF.Abs((shown / scale) - 1) < ScaleTolerance;
        for (var i = 0; i < 4; i++)
            this.Transform(node, i) = exact ? (i is 0 or 3 ? 1 : 0) : transform[i] / scale;

        *widthField = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(width * scale));
        *heightField = (ushort)Math.Min(ushort.MaxValue, Rounding.Round(height * scale));
        *textYOffsetField = (short)Rounding.Round(textYOffset * scale);
        try
        {
            this.drawBakedHook.Original(renderer, node, bake);
        }
        finally
        {
            for (var i = 0; i < 4; i++)
                this.Transform(node, i) = transform[i];
            (*widthField, *heightField) = (width, height);
            *textYOffsetField = textYOffset;
        }
    }

    /// <summary>In <see cref="NamePlateMode.Live"/>, the renderer's own live mode (set by the game's text render mode setting).</summary>
    private void BakePlateDrawDetour(nint renderer, nint node)
    {
        this.SeeRenderer(renderer);
        if (this.EffectiveMode != NamePlateMode.Live)
        {
            this.bakePlateDrawHook.Original(renderer, node);
            return;
        }

        var liveMode = (byte*)(renderer + this.rendererLiveMode);
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
