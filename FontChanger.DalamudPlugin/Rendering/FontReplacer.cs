using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts;

/// <summary>
/// Makes the game's text renderer draw glyphs rasterized at the size they are drawn at.
/// </summary>
/// <remarks>
/// <para>Two hooks, both on the framework thread:</para>
/// <list type="number">
/// <item>The font picker (FUN_140651DC0) chooses the game font for the requested size, which already includes the text
/// node's screen scale. The detour swaps it for a copy whose size is exactly that size (in half pixels), so the
/// renderer's scale (requested size / font size) is 1. The copy keeps the game font's glyph map and textures, with the
/// atlas pages added after them.</item>
/// <item>The glyph lookup (FUN_14064FC50) is answered for a copy from its own glyphs, rasterized on first use. A
/// codepoint no family has, and the private-use icons, fall through to the game's glyph, which the copy can still draw
/// from the game's textures (at the game font's own size).</item>
/// </list>
/// <para>Text is laid out again every frame, so nothing keeps glyph pointers between frames.</para>
/// </remarks>
internal sealed unsafe class FontReplacer : IDisposable
{
    // GameFont* PickFont(GameFontSet* set, bool useCache) (FUN_140651DC0). Unique in 7.56h.
    private const string PickFontSignature = "83 B9 58 01 00 00 00 4C 8B C1 F3 0F 10 91 2C 01 00 00";

    // GameGlyph* GetGlyph(GameFontSet* set, uint utf8Value, GameFont* font) (FUN_14064FC50). Unique in 7.56h.
    private const string GetGlyphSignature = "89 54 24 10 48 83 EC 38 41 F6 80 F2 00 00 00 04 4D 8B C8";

    // void BuildFontCache(AtkTextNode* node) (FUN_1406651D0): frees the node's font cache and lays its text out into a
    // new one (AtkFontAnalyzerCreateCache), whose entries keep glyph pointers for later draws. Called by SetText,
    // FUN_140661D60, ToggleFontCache(true) and AtkComponentTextInput.ApplyMask. Unique in 7.56h.
    private const string BuildFontCacheSignature = "48 85 C9 0F 84 A9 01 00 00 56 48 83 EC 20 48 89 5C 24 30 48 8B F1 0F B7 89 68 01 00 00";

    // bool LayOutCharacter(AtkFontAnalyzerBase* analyzer, byte** text, State* state) (FUN_1406EEC70): decodes the
    // character at *text, looks up its glyph (GetGlyph) with kerning and CJK spacing, hands it to the analyzer (vf6), and
    // steps *text past it. Called for each character by the text walkers. Unique in 7.56h.
    private const string LayOutCharacterSignature =
        "40 53 55 56 57 41 54 41 55 41 56 48 83 EC 70 4C 8B 0A 49 8B F8 48 8B 69 40 4C 8B E2 4C 8B E9 45 0F B6 01 48 8B 85 D8 00";

    // void FreeFontCache(AtkTextNode* node) (FUN_140665140): ToggleFontCache(false). Unique in 7.56h.
    private const string FreeFontCacheSignature = "48 85 C9 74 7A 57 48 83 EC 20 48 8B F9 0F B7 89 68 01 00 00 66 85 C9 74 61 48 8B 05";

    // In AtkFontAnalyzer.ctor: the vtables of AtkFontAnalyzerRenderer (0x14217C4C8) and AtkFontAnalyzerRenderCount
    // (0x14217C408), the only analyzers that use a glyph's place in the atlas; the others (DrawSize, SearchPosition,
    // BuildLink, ...) only take its width. Unique in 7.56h.
    private const string DrawingAnalyzerVtblsSignature =
        "48 8D 05 ?? ?? ?? ?? 48 89 83 18 01 00 00 48 8D 05 ?? ?? ?? ?? 48 89 BB 80 02 00 00";

    // AtkTextNode: the node's slot in the font manager's cache table (+0x88, 0x10 bytes each), 0 for none; and the
    // byte whose bit 6 asks for a cache (ToggleFontCache).
    private const int FontCacheSlotOffset = 0x168;
    private const int FontCacheFlagsOffset = 0x171;
    private const byte UseFontCacheFlag = 0x40;

    // Sizes in half pixels: below 4 px nothing is legible. Up to 255 px, glyphs are drawn at the size asked for, those
    // larger than the game's byte-sized glyph fields cut off (PlaceCell); a larger request uses the 255 px copy, scaled
    // up by the renderer.
    private const int MinHalfPx = 8;
    private const int MaxHalfPx = 2 * byte.MaxValue;

    private const int GlyphBlockSize = 1024;

    private readonly Hook<PickFontDelegate> pickFontHook;
    private readonly Hook<GetGlyphDelegate> getGlyphHook;
    private readonly Hook<FontCacheDelegate> buildFontCacheHook;
    private readonly Hook<LayOutCharacterDelegate> layOutCharacterHook;
    private readonly TextShaper shaper;

    // The character the game is laying out on this thread (FUN_1406EEC70), for GetGlyph to look up in its shaped run.
    [ThreadStatic]
    private static nint currentCharacter;

    // Whether that character is laid out by an analyzer that only measures, so its glyph needs no pixels yet.
    [ThreadStatic]
    private static bool measuringOnly;

    // A glyph of no width: the characters of a shaped cluster after its first.
    private GameGlyph* emptyGlyph;
    private readonly delegate* unmanaged<AtkTextNode*, void> freeFontCache;
    private readonly nint rendererVtbl;
    private readonly nint renderCountVtbl;

    // Glyphs made while measuring, whose pixels go to the atlas when they are first drawn.
    private readonly Dictionary<nint, PendingCell> pendingCells = [];

    // Cells of game glyphs placed in the atlas, whose pixels are read from the game's textures at the next upload.
    private readonly List<GameCell> gameCells = [];
    private readonly GameTextureReader gameTextures = new();

    // The glyphs made from game glyphs, by copy and character, with the game glyph's shape and texture they came from.
    private readonly Dictionary<(nint Copy, uint Utf8), (ulong Shape, nint Texture, nint Glyph)> gameGlyphs = [];
    private readonly GlyphRasterizer rasterizer;
    private readonly GlyphAtlas atlas;

    private readonly Dictionary<(nint Original, int HalfPx), nint> copiesByKey = [];
    private readonly Dictionary<nint, CopyInfo> copies = [];
    private readonly Dictionary<(ReplacementFace Face, int HalfPx), SizedFont> sizedFonts = [];

    // The faces glyphs come from: the preset's by game font name, or the built-in one; and each game font's.
    private readonly ReplacementFace builtInFace;
    private readonly Dictionary<string, ReplacementFace> presetFaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ReplacementFace> faceByFont = [];
    private readonly List<nint> glyphBlocks = [];
    private GameGlyph* glyphBlock;
    private int glyphBlockUsed = GlyphBlockSize;
    private bool enabled = true;

    public FontReplacer()
    {
        try
        {
            this.rasterizer = new();
            this.builtInFace = ReplacementFace.CreateBuiltIn(this.rasterizer);
            try
            {
                GameFontNames.Initialize();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "The game's font names weren't found; presets can't be applied");
            }

            this.atlas = new();
            this.atlas.PageAdded += this.OnPageAdded;
            this.atlas.EnsurePage();
            this.emptyGlyph = (GameGlyph*)NativeMemory.AllocZeroed((nuint)sizeof(GameGlyph));
            this.emptyGlyph->Packed = GameGlyph.Pack(0, 0, 0, GlyphAtlas.FirstTextureIndex);
            this.shaper = new(this.rasterizer, this);
            this.freeFontCache = (delegate* unmanaged<AtkTextNode*, void>)Plugin.SigScanner.ScanText(FreeFontCacheSignature);
            var vtbls = Plugin.SigScanner.ScanText(DrawingAnalyzerVtblsSignature);
            this.rendererVtbl = vtbls + 7 + *(int*)(vtbls + 3);
            this.renderCountVtbl = vtbls + 21 + *(int*)(vtbls + 17);
            this.pickFontHook = Plugin.GameInterop.HookFromSignature<PickFontDelegate>(PickFontSignature, this.PickFontDetour);
            this.getGlyphHook = Plugin.GameInterop.HookFromSignature<GetGlyphDelegate>(GetGlyphSignature, this.GetGlyphDetour);
            this.buildFontCacheHook = Plugin.GameInterop.HookFromSignature<FontCacheDelegate>(
                BuildFontCacheSignature, this.BuildFontCacheDetour);
            this.layOutCharacterHook = Plugin.GameInterop.HookFromSignature<LayOutCharacterDelegate>(
                LayOutCharacterSignature, this.LayOutCharacterDetour);
            Plugin.Framework.Update += this.OnFrameworkUpdate;
            this.getGlyphHook.Enable();
            this.pickFontHook.Enable();
            this.buildFontCacheHook.Enable();
            this.layOutCharacterHook.Enable();

            // Caches built so far hold the game's glyphs.
            this.UpdateFontCaches();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate GameFont* PickFontDelegate(GameFontSet* set, byte useCache);

    private delegate GameGlyph* GetGlyphDelegate(GameFontSet* set, uint utf8Value, GameFont* font);

    private delegate void FontCacheDelegate(AtkTextNode* node);

    private delegate nint LayOutCharacterDelegate(nint analyzer, byte** text, nint state);

    /// <summary>Gets the shaper, which also finds line breaks.</summary>
    public TextShaper Shaper => this.shaper;

    /// <summary>
    /// Raised on the framework thread when text drawn earlier no longer matches (the replacement was switched, or the
    /// glyphs were rebuilt), for what keeps drawn text around (the nameplate bakes).
    /// </summary>
    public event Action? TextInvalidated;

    /// <summary>Gets the face of the game fonts a preset has no face for.</summary>
    public ReplacementFace BuiltInFace => this.builtInFace;

    /// <summary>
    /// Uses a preset's faces for the game fonts of their names (the built-in face for the others, or for all if null),
    /// drawing characters they lack with system fonts if <paramref name="systemFallback"/>, else the game's. Framework
    /// thread, between frames. Every glyph is made again.
    /// </summary>
    public void SetPreset(Preset? preset, bool systemFallback)
    {
        this.shaper.ClearFormats();
        this.DisposePresetFaces();
        this.faceByFont.Clear();
        this.sizedFonts.Clear();
        foreach (var (name, def) in preset?.Faces ?? new Dictionary<string, FaceDef>())
        {
            try
            {
                this.presetFaces[name] = new(this.rasterizer, def, systemFallback, this.builtInFace);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Setting up the face {name} failed; its fonts use the built-in face", name);
            }
        }

        // Copies made so far take the glyphs and metrics of their new faces.
        foreach (var (copy, info) in this.copies.ToArray())
        {
            var updated = info with { Sized = this.GetSized((GameFont*)info.Original, (int)MathF.Round(info.Sized.Px * 2)) };
            this.copies[copy] = updated;
            this.Sync((GameFont*)copy, updated);
        }

        this.RebuildGlyphs();
    }

    /// <summary>Gets the face a game font's glyphs come from.</summary>
    public ReplacementFace GetFace(GameFont* original)
    {
        if (this.faceByFont.TryGetValue((nint)original, out var face))
            return face;
        var name = this.presetFaces.Count != 0 ? GameFontNames.GetFaceName(original) : null;
        face = name is not null && this.presetFaces.TryGetValue(name, out var found) ? found : this.builtInFace;
        this.faceByFont[(nint)original] = face;
        return face;
    }

    private void DisposePresetFaces()
    {
        foreach (var face in this.presetFaces.Values)
            face.Dispose();
        this.presetFaces.Clear();
    }

    /// <summary>Gets the glyphs of a face at a size, made with the metrics they have for a game font.</summary>
    private SizedFont GetSized(GameFont* original, int halfPx)
    {
        var face = this.GetFace(original);
        if (!this.sizedFonts.TryGetValue((face, halfPx), out var sized))
        {
            var px = halfPx / 2f;
            var (ascent, lineHeight) = face.GetLineMetrics(px, original);
            sized = new(face, px, ascent, lineHeight, (nint)original);
            this.sizedFonts.Add((face, halfPx), sized);
        }

        return sized;
    }

    /// <summary>
    /// Gets the edge outline's (FontEdgePS) width at each text size. A font's edge width is the step the shader samples
    /// by, one texel of the texture width the font claims; above 1 px, glyph boxes get empty margins for it, as the edge
    /// is only drawn within a glyph's box and one pixel around it.
    /// </summary>
    public EdgeSettings Edge { get; private set; } = new();

    /// <summary>Sets the edge outline's width at each text size. Framework thread; glyphs are made again.</summary>
    public void SetEdge(EdgeSettings edge)
    {
        edge = edge.Clamped();
        if (edge == this.Edge)
            return;
        this.Edge = edge;

        // The plugin's edge shader draws a round edge of any width, which each font sets by the texture width it claims;
        // unless edges are the game's own (1 px at every size). The game's draws a fixed pattern, made wider only by
        // claiming a narrower texture (blocky past about 1.5 px): the fallback if the plugin's can't be used.
        try
        {
            this.edgeShader.Set(edge != new EdgeSettings());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Using the plugin's edge shader failed; edges are widened with the game's");
            this.edgeShader.Set(false);
        }

        this.RebuildGlyphs();
    }

    private readonly EdgeShader edgeShader = new();

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            this.edgeShader.EnsureInstalled();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Installing the edge shader again failed");
        }
    }

    /// <summary>
    /// Makes a glyph rasterized for a size ready to be placed: adjusted as its element says (when an element drew it), and
    /// surrounded with empty pixels for a wide edge, which is only drawn within a glyph's box and the pixel around it.
    /// </summary>
    internal RasterGlyph FinishGlyph(SizedFont sized, int element, RasterGlyph r)
    {
        if (element >= 0)
            r = sized.Face.Adjust(element, r, sized.Px, (GameFont*)sized.GameFont);
        var m = Math.Max(0, (int)MathF.Ceiling(this.Edge.GetWidth(sized.Px)) - 1);
        if (m == 0 || r.Width == 0)
            return r;
        var w = r.Width + (2 * m);
        var h = r.Height + (2 * m);
        var alpha = new byte[w * h];
        for (var y = 0; y < r.Height; y++)
            r.Alpha.AsSpan(y * r.Width, r.Width).CopyTo(alpha.AsSpan(((y + m) * w) + m));
        return r with { Left = r.Left - m, Top = r.Top - m, Width = w, Height = h, Alpha = alpha };
    }

    /// <summary>
    /// Forgets every glyph and empties the atlas, so they are rasterized again with the current settings. Framework
    /// thread, between frames. Nameplates already baked keep their pixels until the game bakes them again.
    /// </summary>
    public void RebuildGlyphs()
    {
        this.shaper.Clear();
        this.pendingCells.Clear();
        this.gameCells.Clear();
        this.gameGlyphs.Clear();
        foreach (var sized in this.sizedFonts.Values)
            sized.Glyphs.Clear();
        foreach (var block in this.glyphBlocks)
            NativeMemory.Free((void*)block);
        this.glyphBlocks.Clear();
        this.glyphBlockUsed = GlyphBlockSize;
        this.atlas.Clear();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>Gets or sets whether the game draws with the replaced fonts. Framework thread (or the draw callback).</summary>
    public bool Enabled
    {
        get => this.enabled;
        set
        {
            if (this.enabled == value)
                return;
            this.enabled = value;
            ForgetPickedFonts();
            this.UpdateFontCaches();
            this.TextInvalidated?.Invoke();
        }
    }

    public void Dispose()
    {
        // While the hooks are still there: no font set may keep a copy as its cached pick, and no font cache may keep
        // glyphs of a copy. Caches are rebuilt with the game's fonts.
        if (this.pickFontHook is not null && this.getGlyphHook is not null && this.buildFontCacheHook is not null)
            this.Enabled = false;
        Plugin.Framework.Update -= this.OnFrameworkUpdate;
        this.edgeShader.Dispose();
        this.layOutCharacterHook?.Dispose();
        this.buildFontCacheHook?.Dispose();
        this.pickFontHook?.Dispose();
        this.getGlyphHook?.Dispose();
        ForgetPickedFonts();

        foreach (var copy in this.copies.Keys)
            NativeMemory.Free((void*)copy);
        this.copies.Clear();
        this.copiesByKey.Clear();
        this.shaper?.Dispose();
        foreach (var block in this.glyphBlocks)
            NativeMemory.Free((void*)block);
        this.glyphBlocks.Clear();
        if (this.emptyGlyph is not null)
        {
            NativeMemory.Free(this.emptyGlyph);
            this.emptyGlyph = null;
        }

        this.atlas?.Dispose();
        this.gameTextures.Dispose();
        this.DisposePresetFaces();
        this.builtInFace?.Dispose();
        this.rasterizer?.Dispose();
    }

    /// <summary>Makes every font set pick its font again on its next use.</summary>
    private static void ForgetPickedFonts()
    {
        var manager = AtkFontManagerExtras.Instance();
        if (manager is null || manager->FontSets is null)
            return;
        for (var i = 0; i < AtkFontManagerExtras.FontSetCount; i++)
        {
            manager->FontSets[i].CurrentFont = null;
            manager->FontSets[i].CurrentFontSize = 0;
        }
    }

    /// <summary>
    /// A font cache keeps the glyph pointers of the text as laid out when it was built, at the node's unscaled size
    /// (FUN_140665600(node, false)), and draws use them while picking the font for the on-screen size. Glyphs of a copy
    /// only fit the copy of their size, and a copy's glyphs must not outlive the plugin, so while the replacement is on
    /// nodes get no cache: their text is laid out at each draw, as for nodes that never asked for one.
    /// </summary>
    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this.enabled)
            this.freeFontCache(node);
        else
            this.buildFontCacheHook.Original(node);
    }

    /// <summary>
    /// Brings every loaded text node's font cache in line with <see cref="Enabled"/>: freed while on, rebuilt (for the
    /// nodes that ask for one) while off.
    /// </summary>
    private void UpdateFontCaches()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
            return;
        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon is not null)
                this.UpdateFontCaches(&addon->UldManager, 0);
        }
    }

    private void UpdateFontCaches(AtkUldManager* uld, int depth)
    {
        if (uld->NodeList is null || depth > 16)
            return;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
                continue;
            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                var bytes = (byte*)text;
                if (this.enabled)
                {
                    if (*(ushort*)(bytes + FontCacheSlotOffset) != 0)
                        this.freeFontCache(text);
                }
                else if ((bytes[FontCacheFlagsOffset] & UseFontCacheFlag) != 0)
                {
                    this.buildFontCacheHook.Original(text);
                }
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                    this.UpdateFontCaches(&component->UldManager, depth + 1);
            }
        }
    }

    private GameFont* PickFontDetour(GameFontSet* set, byte useCache)
    {
        var font = this.pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cached choice may be a copy picked before: decide again from its game font, since the picker's
        // cache is keyed by its own size, which isn't the one copies are chosen by.
        if (this.copies.TryGetValue((nint)font, out var picked))
            font = (GameFont*)picked.Original;

        if (!this.enabled)
            return font;

        // A font using the texture indices the atlas pages take is left alone.
        if (font->TextureCount > GlyphAtlas.FirstTextureIndex)
            return font;

        try
        {
            // Picked for the on-screen size, even where the game picked for another (the unscaled size of a node with a
            // fixed font resolution, or a pick scale of its own), so the renderer doesn't scale the glyphs.
            var size = set->ScaledSizeY;
            var halfPx = Math.Clamp((int)MathF.Round(size * 2), MinHalfPx, MaxHalfPx);
            var copy = this.GetOrCreateCopy(font, halfPx);

            // The glyphs are rasterized at the size rounded to half pixels, but the copy claims the exact size: the
            // renderer scales by the requested size over this, and anything but exactly 1 resamples every glyph
            // bilinearly (soft text). The glyphs are then up to a quarter pixel off in size, which doesn't show. Past
            // the largest size the renderer has to scale up anyway.
            if (halfPx < MaxHalfPx && halfPx > MinHalfPx)
                copy->Size = size;
            set->CurrentFont = copy;
            return copy;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Replacing a font failed; disabling");
            this.enabled = false;
            ForgetPickedFonts();
            return font;
        }
    }

    private nint LayOutCharacterDetour(nint analyzer, byte** text, nint state)
    {
        var outer = currentCharacter;
        var outerMeasuring = measuringOnly;
        var vtbl = *(nint*)analyzer;
        currentCharacter = (nint)(*text);
        measuringOnly = vtbl != this.rendererVtbl && vtbl != this.renderCountVtbl;
        try
        {
            return this.layOutCharacterHook.Original(analyzer, text, state);
        }
        finally
        {
            currentCharacter = outer;
            measuringOnly = outerMeasuring;
        }
    }

    /// <summary>
    /// Gets what a character a copy has no glyph for is laid out as. FUN_1406EEC70 takes the geta mark (U+3013), else
    /// '-', else U+3400, from the font's glyph map itself rather than through GetGlyph, which would give the game font's
    /// glyph, at its size and with the edge of its texture. The copy's own glyphs of them are given instead.
    /// </summary>
    private GameGlyph* GetMissingGlyph(GameFontSet* set, uint utf8Value, GameFont* font)
    {
        foreach (var substitute in (ReadOnlySpan<uint>)[0xE38093, 0x2D, 0xE39080])
        {
            if (utf8Value == substitute)
                return null;
            var glyph = this.GetGlyphDetour(set, substitute, font);
            if (glyph is not null)
                return glyph;
        }

        return null;
    }

    private GameGlyph* GetGlyphDetour(GameFontSet* set, uint utf8Value, GameFont* font)
    {
        if (!this.copies.TryGetValue((nint)font, out var info))
            return this.getGlyphHook.Original(set, utf8Value, font);

        var sized = info.Sized;

        // Laid out by FUN_1406EEC70 at a known place in the text: the glyph of its shaped cluster. The character there must
        // be the one asked for (the lookups of the game's fallback characters, if any, are not).
        var p = (byte*)currentCharacter;
        if (p is not null && GameUtf8.PackSequence(p, GameUtf8.SequenceLength(*p)) == utf8Value)
        {
            try
            {
                var shaped = this.shaper.TryGetGlyph(sized, p, (nint)this.emptyGlyph);
                if (shaped != 0)
                    return this.WithPixels((GameGlyph*)shaped);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping failed; the character is laid out by itself");
            }
        }

        if (!sized.Glyphs.TryGetValue(utf8Value, out var glyph))
        {
            try
            {
                glyph = (nint)this.CreateGlyph(sized, utf8Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Rasterizing 0x{value:X} at {px} px failed", utf8Value, sized.Px);
                glyph = 0;
            }

            sized.Glyphs.Add(utf8Value, glyph);
        }

        if (glyph != 0)
            return this.WithPixels((GameGlyph*)glyph);

        // The game's glyph, looked up in the game font's map as it is now. The size the pick set stays.
        var claimedSize = font->Size;
        this.Sync(font, info);
        font->Size = claimedSize;
        var game = this.getGlyphHook.Original(set, utf8Value, font);
        if (game is null)
            return currentCharacter != 0 ? this.GetMissingGlyph(set, utf8Value, font) : null;

        try
        {
            var own = this.GetGameGlyph(font, info, game, utf8Value);
            return own is null ? game : this.WithPixels(own);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
            return game;
        }
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph, scaled to the copy's size: drawn from the game's texture, the edge
    /// would step by texels of the atlas's width (all of a font's textures share one), not the game texture's. Its pixels
    /// are read from the game's texture at the next upload. Null to draw the game's glyph as it is.
    /// </summary>
    private GameGlyph* GetGameGlyph(GameFont* copy, CopyInfo info, GameGlyph* game, uint utf8Value)
    {
        var textureIndex = game->TextureIndex;
        var texture = textureIndex < GlyphAtlas.FirstTextureIndex ? copy->Textures[textureIndex].Value : null;
        var shape = *(ulong*)&game->Packed;

        // The game rebuilds its fonts in place: a glyph made from a game glyph that changed since is made again.
        var key = ((nint)copy, utf8Value);
        if (this.gameGlyphs.TryGetValue(key, out var made) && made.Shape == shape && made.Texture == (nint)texture)
            return (GameGlyph*)made.Glyph;

        GameGlyph* glyph = null;
        if (texture is not null && GameTextureReader.CanRead(texture))
        {
            var original = (GameFont*)info.Original;
            var sized = info.Sized;
            var scale = sized.Px / original->Size;
            var top = game->OffsetY - original->Ascent;
            var (left, t, w, h) = RasterGlyph.ScaledBounds(0, top, game->Width, game->Height, scale, 0, 0);
            var r = new RasterGlyph((int)MathF.Round((game->Width + game->OffsetX) * scale), left, t, w, h, new byte[w * h])
            {
                Source = new((nint)texture, game->X, game->Y, game->Width, game->Height, game->Channel, 0, top, scale),
            };
            glyph = this.PlaceCell(sized, this.FinishGlyph(sized, ReplacementFace.GameElement, r), utf8Value);
        }

        this.gameGlyphs[key] = (shape, (nint)texture, (nint)glyph);
        return glyph;
    }

    /// <summary>
    /// Fills the cells of game glyphs from the game's textures, then copies the atlas's changes to the GPU. On the thread
    /// that calls Present.
    /// </summary>
    public void Upload()
    {
        foreach (var c in this.gameCells)
        {
            try
            {
                var s = c.Cell.Raster.Source!;
                var pixels = this.gameTextures.Read((FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture*)s.Texture, s.X, s.Y, s.Width, s.Height, s.Plane);
                this.WriteCell((GameGlyph*)c.Glyph, c.Cell with { Raster = c.Cell.Raster.WithSourcePixels(pixels) }, c.Page, c.Plane, c.X, c.Y);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Reading a game glyph's pixels failed");
            }
        }

        this.gameCells.Clear();
        this.atlas.Upload();
    }

    /// <summary>
    /// Refreshes what a copy borrows from its game font (glyph map, glyphs, textures, flags), keeping its own size,
    /// metrics and pages. The game rebuilds a font in place (FUN_14064ECF0, on a font reload), freeing its glyph map and
    /// glyphs, so a copy must never use what it borrowed at an earlier point.
    /// </summary>
    private void Sync(GameFont* copy, CopyInfo info)
    {
        var sized = info.Sized;
        *copy = *(GameFont*)info.Original;
        copy->Size = sized.Px;
        copy->Ascent = sized.Ascent;
        copy->LineHeight = sized.LineHeight;

        // Neither the narrower-glyph substitution nor the game's kerning pairs apply to these glyphs.
        copy->Secondary = null;
        copy->SecondaryRatio = 0;
        copy->KerningCount = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex): the pages', which the
        // game font's textures may not share (the lobby fonts' are smaller), else outlines sample several texels apart.
        // Claiming a narrower width makes the step, and so the edge, that many times wider; the plugin's edge shader takes
        // the step as its radius.
        var width = (ushort)Math.Clamp(MathF.Round(GlyphAtlas.Size / this.Edge.GetWidth(sized.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;
        this.ApplyPages(copy);
    }

    private GameFont* GetOrCreateCopy(GameFont* original, int halfPx)
    {
        if (this.copiesByKey.TryGetValue(((nint)original, halfPx), out var existing))
        {
            this.Sync((GameFont*)existing, this.copies[existing]);
            return (GameFont*)existing;
        }

        var sized = this.GetSized(original, halfPx);
        var copy = (GameFont*)NativeMemory.Alloc((nuint)sizeof(GameFont));
        var info = new CopyInfo((nint)original, sized);
        this.Sync(copy, info);
        this.copies.Add((nint)copy, info);
        this.copiesByKey.Add(((nint)original, halfPx), (nint)copy);
        return copy;
    }

    /// <summary>A copy's game font, and the glyphs of its face at its size.</summary>
    private readonly record struct CopyInfo(nint Original, SizedFont Sized);

    private void ApplyPages(GameFont* copy)
    {
        for (var i = 0; i < this.atlas.PageCount; i++)
            copy->Textures[GlyphAtlas.FirstTextureIndex + i] = this.atlas.GetKernelTexture(i);
        copy->TextureCount = (ushort)(GlyphAtlas.FirstTextureIndex + this.atlas.PageCount);
    }

    private void OnPageAdded()
    {
        foreach (var copy in this.copies.Keys)
            this.ApplyPages((GameFont*)copy);
    }

    /// <summary>Rasterizes a glyph into the atlas; null to leave it to the game.</summary>
    private GameGlyph* CreateGlyph(SizedFont sized, uint utf8Value)
    {
        var codepoint = GameUtf8.Unpack(utf8Value);

        // A broken sequence, or a codepoint the face leaves to the game, stays the game's.
        if (codepoint < 0)
            return null;
        return sized.Face.TryRasterize(codepoint, sized.Px, (GameFont*)sized.GameFont, out var r, out var element)
            ? this.PlaceCell(sized, this.FinishGlyph(sized, element, r), utf8Value)
            : null;
    }

    /// <summary>
    /// Puts a rasterized glyph or cluster into the atlas as a game glyph advancing by <see cref="RasterGlyph.Advance"/>.
    /// Its pixels go to the atlas when it is first drawn.
    /// </summary>
    internal GameGlyph* PlaceCell(SizedFont sized, RasterGlyph r, uint utf8Value)
    {
        // The game has no left bearing: the glyph's box starts at the pen. Ink left of the pen (an overhang) moves right
        // into the box instead. A box wider than the game's byte-sized field cuts the ink off on the right.
        var boxLeft = Math.Min(r.Left, 0);
        var width = r.Width == 0 ? 0 : Math.Min(r.Left + r.Width - boxLeft, byte.MaxValue);

        // A box is a full line high, as the game's are: italics shear each quad by moving its top edge by an amount based
        // on the line height, which only slants the glyphs of a line alike if all their quads span the same rows. Ink
        // above or below the line (stacked marks, a fallback font's taller script) grows the box instead of being cut
        // off; only such a glyph slants a little less. Measuring uses the line height, not the box.
        var inkTop = sized.Ascent + r.Top;
        var top = 0;
        var height = sized.LineHeight;
        if (width != 0)
        {
            top = Math.Clamp(inkTop, sbyte.MinValue, 0);
            height = Math.Min(Math.Max(sized.LineHeight, inkTop + r.Height) - top, byte.MaxValue);
        }

        var glyph = this.AllocateGlyph();
        glyph->Utf8Value = utf8Value;
        glyph->Packed = GameGlyph.Pack(0, 0, 0, GlyphAtlas.FirstTextureIndex);
        glyph->Width = (byte)width;
        glyph->Height = (byte)height;
        glyph->OffsetX = (sbyte)Math.Clamp(r.Advance - width, sbyte.MinValue, sbyte.MaxValue);
        glyph->OffsetY = (sbyte)top;
        if (width == 0)
            return glyph;

        // Text is measured far more than drawn (every text change, at the node's unscaled size too): a glyph only
        // measured so far keeps its pixels here until it is drawn.
        var cell = new PendingCell(r, boxLeft, inkTop - top);
        if (measuringOnly)
            this.pendingCells.Add((nint)glyph, cell);
        else
            this.WritePixels(glyph, cell);
        return glyph;
    }

    /// <summary>Puts a glyph's pixels into the atlas if it was only measured so far; for a glyph about to be drawn.</summary>
    private GameGlyph* WithPixels(GameGlyph* glyph)
    {
        if (!measuringOnly && this.pendingCells.Remove((nint)glyph, out var cell))
        {
            try
            {
                this.WritePixels(glyph, cell);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Placing a glyph in the atlas failed");
                DropPixels(glyph);
            }
        }

        return glyph;
    }

    private void WritePixels(GameGlyph* glyph, PendingCell cell)
    {
        if (!this.atlas.TryAllocate(glyph->Width, glyph->Height, out var page, out var plane, out var x, out var y))
        {
            DropPixels(glyph);
            return;
        }

        glyph->Packed = GameGlyph.Pack(x, y, plane, GlyphAtlas.FirstTextureIndex + page);
        if (cell.Raster.Source is not null)
            this.gameCells.Add(new((nint)glyph, cell, page, plane, x, y));
        else
            this.WriteCell(glyph, cell, page, plane, x, y);
    }

    private void WriteCell(GameGlyph* glyph, PendingCell cell, int page, int plane, int x, int y)
    {
        var r = cell.Raster;

        // The ink's rows in the box (cut off only past the game's byte-sized fields).
        var firstRow = Math.Max(0, -cell.InkTop);
        var lastRow = Math.Min(r.Height, glyph->Height - cell.InkTop);
        if (lastRow > firstRow)
        {
            this.atlas.Write(
                page,
                plane,
                x,
                y + cell.InkTop + firstRow,
                glyph->Width,
                lastRow - firstRow,
                r.Alpha.AsSpan(firstRow * r.Width, (lastRow - firstRow) * r.Width),
                r.Width,
                r.Left - cell.BoxLeft);
        }
    }

    /// <summary>Makes a glyph whose pixels found no room draw nothing, advancing as measured.</summary>
    private static void DropPixels(GameGlyph* glyph)
    {
        glyph->OffsetX = (sbyte)Math.Clamp(glyph->OffsetX + glyph->Width, sbyte.MinValue, sbyte.MaxValue);
        glyph->Width = 0;
    }

    /// <summary>A glyph's coverage not yet in the atlas: the ink's left in the box, and its top row in the box.</summary>
    private readonly record struct PendingCell(RasterGlyph Raster, int BoxLeft, int InkTop);

    /// <summary>A game glyph's cell in the atlas, waiting for its pixels.</summary>
    private readonly record struct GameCell(nint Glyph, PendingCell Cell, int Page, int Plane, int X, int Y);

    private GameGlyph* AllocateGlyph()
    {
        if (this.glyphBlockUsed == GlyphBlockSize)
        {
            this.glyphBlock = (GameGlyph*)NativeMemory.AllocZeroed((nuint)(GlyphBlockSize * sizeof(GameGlyph)));
            this.glyphBlocks.Add((nint)this.glyphBlock);
            this.glyphBlockUsed = 0;
        }

        return &this.glyphBlock[this.glyphBlockUsed++];
    }

    /// <summary>
    /// The glyphs of a face at one pixel size, shared by the copies of the game fonts of that face at that size (a font
    /// and its lobby version). <see cref="GameFont"/> is the first of them, for the game's glyphs and metrics.
    /// </summary>
    internal sealed class SizedFont(ReplacementFace face, float px, int ascent, int lineHeight, nint gameFont)
    {
        public ReplacementFace Face { get; } = face;

        public nint GameFont { get; } = gameFont;

        public float Px { get; } = px;

        public int Ascent { get; } = ascent;

        public int LineHeight { get; } = lineHeight;

        /// <summary>By packed UTF-8 value; 0 for a glyph left to the game.</summary>
        public Dictionary<uint, nint> Glyphs { get; } = [];
    }
}
