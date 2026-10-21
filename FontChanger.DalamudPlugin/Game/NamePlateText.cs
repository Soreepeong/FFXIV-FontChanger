using System;
using System.Collections.Generic;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using static FFXIVClientStructs.FFXIV.Client.UI.AddonNamePlate;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>
/// The game bakes each plate's text once per change at the text node's unscaled size and resamples it at every distance, so this
/// bakes it at its on-screen size at 100 percent instead.
/// </summary>
internal sealed unsafe class NamePlateText : IDisposable
{
    private const string AddonName = "NamePlate";

    // OnRequestedUpdate (SetCommonNamePlate's body, inlined in 7.56h) never scales a plate below this factor.
    private const float MinDistanceFactor = 0.05f;

    // Bake scales beyond these are not worth it (or are a measuring error).
    private const float MinBakeScale = 0.5f;
    private const float MaxBakeScale = 4f;

    // A plate shown this much larger than its bake is baked again; within this of its bake scale it is drawn at exactly 1.
    private const float ScaleTolerance = 0.02f;

    private static readonly nint AddonBakePlateOffset = typeof(AddonNamePlate).FieldOffsetOf(nameof(AddonNamePlate.BakePlate));

    private readonly FontReplacer _replacer;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();
    private readonly Hook<AllocateBakeDelegate> _allocateBakeHook;
    private readonly Hook<PrepareDelegate> _prepareHook;
    private readonly Hook<DrawBakedDelegate> _drawBakedHook;
    private readonly Hook<BakePlateDrawDelegate> _bakePlateDrawHook;

    // Keyed by the object's BakeData, which is its first member.
    private readonly Dictionary<Pointer<BakeData>, float> _bakeScales = [];

    // By name text node, as measuring isn't told whose text it measures.
    private readonly Dictionary<nint, int> _objectIndices = [];

    // By object index, as of the game's last update. The character's name's line isn't the title's.
    private readonly NodeFaces[] _faces = new NodeFaces[NumNamePlateObjects];

    private BakePlateRenderer* _lastRenderer;
    private bool _rebakePending;

    // The game measures a plate's text only when the text changes.
    private bool _remeasurePending = true;

    private NamePlateMode _mode = NamePlateMode.BakedAtFullSize;

    public NamePlateText(FontReplacer replacer)
    {
        this._replacer = replacer;
        try
        {
            var allocateBake = BakePlateRenderer.Addresses.AllocateBake.Value;
            var drawBaked = BakePlateRenderer.Addresses.DrawBaked.Value;

            // Draw and PrepareNode are virtual, so the hooks go where the renderer's table points.
            var vtable = BakePlateRenderer.StaticVirtualTablePointer;
            if (vtable is null)
                throw new InvalidOperationException("The nameplate renderer's table isn't in this version of the game.");
            var prepare = (nint)vtable->PrepareNode;
            var bakePlateDraw = (nint)vtable->Draw;

            // Undone in reverse, so plates are marked for rebaking (and drawn live until then) before the hooks that know their bake scale go.
            this._drawBakedHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<DrawBakedDelegate>(drawBaked, this.DrawBakedDetour));
            this._prepareHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<PrepareDelegate>(prepare, this.PrepareDetour));
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
            this._replacer.TextInvalidated += this.OnTextInvalidated;
            this._finalizer.Add(() => this._replacer.TextInvalidated -= this.OnTextInvalidated);
            Array.Fill(this._faces, new(TextFace.NamePlate, TextFace.NamePlateTitle));
            this._replacer.AddNodeFaces(this.GetNodeFaces);
            this._finalizer.Add(() => this._replacer.RemoveNodeFaces(this.GetNodeFaces));
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, AddonName, this.OnPreRequestedUpdate);
            this._finalizer.Add(() => Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, AddonName, this.OnPreRequestedUpdate));
            Plugin.Framework.Update += this.OnFrameworkUpdate;
            this._finalizer.Add(() => Plugin.Framework.Update -= this.OnFrameworkUpdate);
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

    public NamePlateMode Mode
    {
        get => Misc.DebugAssertMainThread(this._mode);
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

    public void Dispose()
    {
        Misc.DebugAssertMainThread();
        this._finalizer.Dispose();
    }

    // The plates are reachable only through the renderer a hook last saw, which is embedded in the NamePlate addon.
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

    private void SeeRenderer(BakePlateRenderer* renderer)
    {
        if (this._lastRenderer != renderer)
            this.ReadNameTexts((AddonNamePlate*)((byte*)renderer - AddonBakePlateOffset));
        this._lastRenderer = renderer;
        if (this._rebakePending)
            this.ForceRebake();
    }

    private void ReadNameTexts(AddonNamePlate* addon)
    {
        this._objectIndices.Clear();
        var objects = addon->NamePlateObjectArray;
        if (objects is null)
            return;
        for (var i = 0; i < NumNamePlateObjects; i++)
        {
            if (objects[i].NameText is not null)
                this._objectIndices[(nint)objects[i].NameText] = i;
        }
    }

    // OnRequestedUpdate measures plates' text, possibly before any is drawn.
    private void OnPreRequestedUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AddonNamePlate*)args.Addon.Address;
            this.ReadNameTexts(addon);
            this.ReadCharacters(addon);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Reading the nameplates' text nodes failed");
        }
    }

    // A plate whose face changes is updated (and so measured) again, as the game would only if its text changed.
    private void ReadCharacters(AddonNamePlate* addon)
    {
        var objects = addon->NamePlateObjectArray;
        var array = NamePlateNumberArray.Instance();
        var ui = UIModule.Instance();
        if (objects is null || array is null || ui is null)
            return;
        var ui3d = ui->GetUI3DModule();
        var atk = ui->GetRaptureAtkModule();
        var shown = Math.Clamp(array->ActiveNamePlateCount, 0, NumNamePlateObjects);
        for (var i = 0; i < shown; i++)
        {
            ref var data = ref array->ObjectData[i];
            var index = data.NamePlateObjectIndex;
            if (index < 0 || index >= NumNamePlateObjects)
                continue;

            // As Dalamud's nameplate API finds it: NPCs have no entity ID, and an invalid object's plate (in some cutscenes) no object.
            var objectInfo = atk->NamePlateInfoEntries[index].ObjectId.ObjectId != 0xE0000000 && i < ui3d->NamePlateObjectInfoCount
                                 ? ui3d->NamePlateObjectInfoPointers[i].Value
                                 : null;
            var obj = objectInfo is null || objectInfo->GameObject is null ? null : Plugin.ObjectTable[objectInfo->GameObject->ObjectIndex];
            var faces = this.ChooseFaces(obj, data.NamePlateKind);
            var old = this._faces[index];
            this._faces[index] = faces;
            if (old.Face == faces.Face && old.OtherLines == faces.OtherLines)
                continue;
            if (objects[index].NameText is not null)
                objects[index].NameText->NodeText.Clear();
            data.UpdateFlags |= 1;
        }
    }

    // In order, where it has fonts: the character's rule's (its world's before any world's), its kind's, then the nameplates'.
    private NodeFaces ChooseFaces(IGameObject? obj, UIObjectKind kind)
    {
        var name = obj?.Name.TextValue ?? string.Empty;
        var world = obj is IPlayerCharacter pc && pc.HomeWorld.ValueNullable is { } row ? row.Name.ExtractText() : string.Empty;
        var rule = name.Length == 0 ? null
                   : TextFaceNames.TryGetNamePlateRule(TextFaceNames.NamePlateRuleName(name, world), out _) ? TextFaceNames.NamePlateRuleName(name, world)
                   : TextFaceNames.TryGetNamePlateRule(TextFaceNames.NamePlateRuleName(name, string.Empty), out _) ? TextFaceNames.NamePlateRuleName(name, string.Empty)
                   : null;

        var face = rule is not null && this.WithFonts(rule) is { } ruleFace ? ruleFace
                   : this._replacer.HasOwnFonts(TextFaceNames.KindFace(kind)) ? TextFaceNames.KindFace(kind)
                   : TextFace.NamePlate;
        var title = rule is not null && this.WithFonts(TextFaceNames.NamePlateRuleTitleName(rule)) is { } ruleTitle ? ruleTitle : TextFace.NamePlateTitle;
        return new(face, title, name.Length == 0 ? null : Encoding.UTF8.GetBytes(name));
    }

    private TextFace? WithFonts(string rule) =>
        TextFaceNames.TryGetNamePlateRule(rule, out var face) && this._replacer.HasOwnFonts(face) ? face : null;

    private NodeFaces? GetNodeFaces(Pointer<AtkTextNode> node) =>
        this._objectIndices.TryGetValue((nint)node.Value, out var index) ? this._faces[index] : null;

    private void OnTextInvalidated()
    {
        this._remeasurePending = true;
        this.ForceRebake();
    }

    // Between frames, as the game's update may be what invalidated the text (a font it built).
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this._remeasurePending || this._lastRenderer is null)
            return;
        var addon = (AddonNamePlate*)((byte*)this._lastRenderer - AddonBakePlateOffset);
        if (!IsLoaded(&addon->AtkUnitBase))
            return;

        this._remeasurePending = false;
        try
        {
            // The update measures again the text that differs from its node's. Plates not shown are left alone, as the game may
            // show them again without updating them.
            var objects = addon->NamePlateObjectArray;
            var array = NamePlateNumberArray.Instance();
            if (objects is null || array is null)
                return;
            var shown = Math.Clamp(array->ActiveNamePlateCount, 0, NumNamePlateObjects);
            for (var i = 0; i < shown; i++)
            {
                var index = array->ObjectData[i].NamePlateObjectIndex;
                if (index >= 0 && index < NumNamePlateObjects && objects[index].NameText is not null)
                    objects[index].NameText->NodeText.Clear();
            }

            addon->UpdateAllNamePlates();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Measuring the nameplates' text again failed");
        }
    }

    // The transform is a 2x2 matrix of floats, row by row.
    private static ref float Transform(AtkResNode* node, int index) => ref ((float*)&node->Transform)[index];

    // OnRequestedUpdate scaled the plate by this update's distance factor, which it stored as the bake alpha, factor^(1/4) * 255.
    private static bool TryGetFullSizeScale(NamePlateObject* obj, out float fullSize)
    {
        fullSize = 0;
        var text = (AtkResNode*)obj->NameText;
        if (text is null)
            return false;
        var shown = 1f;
        for (var node = text; node is not null; node = node->ParentNode)
            shown *= node->ScaleX;
        if (!float.IsFinite(shown) || shown <= 0)
            return false;
        var a = obj->BakeData.Alpha / 255f;
        fullSize = shown / Math.Max(a * a * a * a, MinDistanceFactor);
        return float.IsFinite(fullSize);
    }

    // OnRequestedUpdate calls this for plates with NeedsToBeBaked, which it clears on success.
    private byte AllocateBakeDetour(BakePlateRenderer* renderer, NamePlateObject* obj)
    {
        this.SeeRenderer(renderer);
        Pointer<BakeData> bake = &obj->BakeData;
        var previous = this._bakeScales.GetValueOrDefault(bake);
        this._bakeScales.Remove(bake);
        if (this.EffectiveMode != NamePlateMode.BakedAtFullSize || !TryGetFullSizeScale(obj, out var fullSize))
            return this._allocateBakeHook.Original(renderer, obj);

        // Never below the previous bake scale, since the plate was shown that large.
        var scale = Math.Clamp(Math.Max(fullSize, previous), MinBakeScale, MaxBakeScale);

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
        this._replacer.ApplyNodeFace(set, node, this.GetNodeFaces(node) ?? new NodeFaces(TextFace.NamePlate));

        // The original sets the node scale to 1, so a region allocated at another scale needs its own.
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

        // Plates shown over 100 percent up close or when targeted outgrow their bake, and are drawn live until baked again.
        var obj = (NamePlateObject*)bake;
        if (shown > scale * (1 + ScaleTolerance) && !obj->NeedsToBeBaked && this.EffectiveMode == NamePlateMode.BakedAtFullSize)
            obj->NeedsToBeBaked = true;

        // The original point samples only under an exact identity transform (bilinear blurs moving plates even at 1:1), so the
        // node is given in bake pixels and a near-1 transform snaps to identity, as plates aren't rotated.
        var (width, height) = (node->Width, node->Height);
        var textYOffset = bake->TextYOffset;
        var exact = MathF.Abs((shown / scale) - 1) < ScaleTolerance;
        for (var i = 0; i < 4; i++)
            Transform(node, i) = exact ? i is 0 or 3 ? 1 : 0 : transform[i] / scale;

        node->Width = (ushort)Math.Min(ushort.MaxValue, Misc.StdRound(width * scale));
        node->Height = (ushort)Math.Min(ushort.MaxValue, Misc.StdRound(height * scale));
        bake->TextYOffset = (short)Misc.StdRound(textYOffset * scale);
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

    // Live mode borrows the renderer's own live mode, which the game's text render mode setting normally controls.
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
