using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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
/// <para>Text is laid out again every frame, so nothing keeps glyph pointers between frames. What a copy borrows from its
/// game font is taken again when the game builds that font again (BuildFont, the only place it does).</para>
/// </remarks>
internal sealed unsafe class FontReplacer : IDisposable
{
    // Sizes in half pixels: below 4 px nothing is legible. Up to 255 px, glyphs are drawn at the size asked for, those
    // larger than the game's byte-sized glyph fields cut off (PlaceCell); a larger request uses the 255 px copy, scaled
    // up by the renderer.
    private const int MinHalfPx = 8;
    private const int MaxHalfPx = 2 * byte.MaxValue;

    private const int SlotBlockSize = 1024;

    // When the atlas is full, a plane is emptied of glyphs not drawn this recently; at most this often, as text that needs
    // more than fits would otherwise empty one every frame (what doesn't fit draws nothing until the next).
    private const long KeepDrawnMs = 2000;
    private const long EvictIntervalMs = 1000;

    private readonly IHostHook<PickFontDelegate> pickFontHook;
    private readonly IHostHook<GetGlyphDelegate> getGlyphHook;
    private readonly IHostHook<FontCacheDelegate> buildFontCacheHook;
    private readonly IHostHook<LayOutCharacterDelegate> layOutCharacterHook;
    private readonly IHostHook<BuildFontDelegate> buildFontHook;
    private readonly TextShaper shaper;

    // The character the game is laying out on this thread (FUN_1406EEC70).
    [ThreadStatic]
    private static LayoutContext current;

    // The italic bit of the layout state's flags, and of GameFontSet.DrawFlags (which the text node's italics go to only
    // when drawing; the italic macro sets the copy's); the offsets of both flags, -1 if unknown (italics are left sheared).
    private uint stateItalicFlag;
    private uint setItalicFlag;
    private int stateFlagsOffset = -1;
    private int setFlagsOffset = -1;

    // What FUN_1406EEC70 lays out a character a font has no glyph of as, in order of preference (packed UTF-8 values).
    private int[] missingGlyphSubstitutes = [];

    // A glyph of no width: the characters of a shaped cluster after its first.
    private GlyphSlot* emptyGlyph;
    private readonly delegate* unmanaged<nint, void> freeFontCache;
    private nint rendererVtbl;
    private nint renderCountVtbl;

    // AtkTextNode: the node's entry in the font manager's caches, 0 for none; the byte of its cache flags, and the flag
    // that asks for a cache (ToggleFontCache).
    private int fontCacheSlotOffset;
    private int fontCacheFlagsOffset;
    private byte useFontCacheFlag;

    // The glyphs made: blocks of slots the game reads them from (they must not move), and what is kept of each, by its
    // slot's id.
    private readonly List<nint> slotBlocks = [];
    private readonly List<Cell> cells = [];
    private long frameTick;

    // The atlases that ran out of room since they were last made room in, and when that was.
    private readonly HashSet<GlyphAtlas> fullAtlases = [];
    private readonly Dictionary<GlyphAtlas, long> lastEviction = [];

    // Glyphs made from game glyphs, placed in the atlas, whose pixels are read from the game's textures at the next upload.
    private readonly List<nint> gameReads = [];
    private readonly GameTextureReader gameTextures = new();
    private readonly GlyphRasterizer rasterizer;

    // The glyphs of each game font family go to an atlas of its own, so that one family's text can't push another's out:
    // AXIS, the family of most of the UI's text, has pages of the game's size, the others smaller ones.
    private const int AxisAtlasSize = 4096;
    private const int OtherAtlasSize = 2048;
    private readonly Dictionary<string, GlyphAtlas> atlases = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(nint Original, int HalfPx), nint> copiesByKey = [];
    private readonly Dictionary<nint, CopyInfo> copies = [];
    private readonly Dictionary<(ReplacementFace Face, int HalfPx, string FontName), SizedFont> sizedFonts = [];

    // The faces glyphs come from: the preset's by game font name, or the built-in one; and each game font's.
    private readonly ReplacementFace builtInFace;

    // The face of game fonts no preset gives one: their own glyphs, with fallbacks for what they lack.
    private ReplacementFace gameFace = null!;
    private readonly Dictionary<string, ReplacementFace> presetFaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ReplacementFace> faceByFont = [];
    private bool enabled = true;

    public FontReplacer()
    {
        try
        {
            // The game's functions, and the layouts of what they use.
            nint pickFont = 0, getGlyph = 0, layOutCharacter = 0, buildFontCache = 0, freeFontCache = 0, buildFont = 0;
            GameLayout.Resolve("Font replacement", () =>
            {
                GameFontStructs.Resolve();
                GameUi.ResolveUnits();
                GameUi.ResolveTextures();
                pickFont = GameLayout.Address("PickFont");
                getGlyph = GameLayout.Address("GetGlyph");
                layOutCharacter = GameLayout.Address("LayOutCharacter");
                buildFontCache = GameLayout.Address("BuildFontCache");
                freeFontCache = GameLayout.Address("FreeFontCache");
                buildFont = GameLayout.Address("BuildFont");
                this.rendererVtbl = GameLayout.Address("FontAnalyzerVtables", "AtkFontAnalyzerRenderer.Vtable");
                this.renderCountVtbl = GameLayout.Address("FontAnalyzerVtables", "AtkFontAnalyzerRenderCount.Vtable");
                this.fontCacheSlotOffset = GameLayout.Get("AtkTextNode.FontCacheSlot");
                this.fontCacheFlagsOffset = GameLayout.Get("AtkTextNode.FontCacheFlags");
                this.useFontCacheFlag = (byte)GameLayout.Get("AtkTextNode.FontCacheFlags.UseFontCache");
                this.missingGlyphSubstitutes = GameLayout.GetList("LayOutCharacter.MissingGlyphSubstitutes");

                // Optional: without them, italics are the game's shear.
                this.stateFlagsOffset = GameLayout.TryGet("FontAnalyzerState.Flags") ?? -1;
                this.setFlagsOffset = GameLayout.TryGet("GameFontSet.DrawFlags") ?? -1;
                this.stateItalicFlag = (uint)GameLayout.Get("FontAnalyzerState.Flags.Italic");
                this.setItalicFlag = (uint)GameLayout.Get("GameFontSet.DrawFlags.Italic");

                // The functions found call each other.
                if (getGlyph != 0 && layOutCharacter != 0 && GameLayout.Address("LayOutCharacter", "GetGlyph") != getGlyph)
                    throw new InvalidOperationException("LayOutCharacter doesn't call the GetGlyph found.");
                if (buildFontCache != 0 && freeFontCache != 0 && GameLayout.Match("ToggleFontCache") is { } toggle
                    && (toggle.Target("BuildFontCache") != buildFontCache || toggle.Target("FreeFontCache") != freeFontCache))
                    throw new InvalidOperationException("ToggleFontCache doesn't call the BuildFontCache and FreeFontCache found.");
            });
            ClientStructsCheck.Run();
            GlyphAtlas.FirstTextureIndex = GameFontTables.GetMaxTextureCount();

            this.rasterizer = new();
            this.builtInFace = ReplacementFace.CreateBuiltIn(this.rasterizer);
            this.gameFace = ReplacementFace.CreateGame(this.rasterizer, true, this.builtInFace);
            try
            {
                GameFontNames.Initialize();
            }
            catch (Exception ex)
            {
                Host.Log.Warning(ex, "The game's font names weren't found; presets can't be applied");
            }

            this.emptyGlyph = (GlyphSlot*)NativeMemory.AllocZeroed((nuint)sizeof(GlyphSlot));
            this.emptyGlyph->Glyph.Packed = GameGlyph.Pack(0, 0, 0, GlyphAtlas.FirstTextureIndex);
            this.emptyGlyph->Id = -1;
            this.shaper = new(this.rasterizer, this);
            this.freeFontCache = (delegate* unmanaged<nint, void>)freeFontCache;
            var host = Host.Current;
            this.pickFontHook = host.Hook<PickFontDelegate>(pickFont, this.PickFontDetour);
            this.getGlyphHook = host.Hook<GetGlyphDelegate>(getGlyph, this.GetGlyphDetour);
            this.buildFontCacheHook = host.Hook<FontCacheDelegate>(buildFontCache, this.BuildFontCacheDetour);
            this.layOutCharacterHook = host.Hook<LayOutCharacterDelegate>(layOutCharacter, this.LayOutCharacterDetour);
            this.buildFontHook = host.Hook<BuildFontDelegate>(buildFont, this.BuildFontDetour);
            this.getGlyphHook.Enable();
            this.pickFontHook.Enable();
            this.buildFontCacheHook.Enable();
            this.layOutCharacterHook.Enable();
            this.buildFontHook.Enable();

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

    private delegate void FontCacheDelegate(nint node);

    private delegate nint LayOutCharacterDelegate(nint analyzer, byte** text, nint state);

    private delegate int BuildFontDelegate(nint manager, ushort index);

    /// <summary>Gets the shaper, which also finds line breaks.</summary>
    public TextShaper Shaper => this.shaper;

    /// <summary>
    /// Raised on the framework thread when text drawn earlier no longer matches (the replacement was switched, or the
    /// glyphs were rebuilt), for what keeps drawn text around (the nameplate bakes).
    /// </summary>
    public event Action? TextInvalidated;

    /// <summary>Gets the built-in face: system fonts, which draw what faces lack glyph by glyph.</summary>
    public ReplacementFace BuiltInFace => this.builtInFace;

    /// <summary>
    /// Uses a preset's faces for the game fonts of their names (the game's glyphs for the others, or for all if null),
    /// drawing characters they lack with system fonts if <paramref name="systemFallback"/>, else the game's. Framework
    /// thread, between frames. Every glyph is made again.
    /// </summary>
    public void SetPreset(Preset? preset, bool systemFallback)
    {
        this.shaper.ClearFormats();
        this.DisposePresetFaces();
        this.faceByFont.Clear();
        this.sizedFonts.Clear();
        this.gameFace = ReplacementFace.CreateGame(this.rasterizer, systemFallback, this.builtInFace);
        foreach (var (name, def) in preset?.Faces ?? new Dictionary<string, FaceDef>())
        {
            try
            {
                this.presetFaces[name] = new(this.rasterizer, def, systemFallback, this.builtInFace);
            }
            catch (Exception ex)
            {
                Host.Log.Error(ex, "Setting up the face {name} failed; its fonts use the game's glyphs", name);
            }
        }

        // Copies made so far take the glyphs and metrics of their new faces.
        foreach (var (copy, info) in this.copies)
        {
            info.Sized = this.GetSized((GameFont*)info.Original, info.HalfPx);
            this.Sync((GameFont*)copy, info);
        }

        this.RebuildGlyphs();
    }

    /// <summary>Gets the face a game font's glyphs come from.</summary>
    public ReplacementFace GetFace(GameFont* original)
    {
        if (this.faceByFont.TryGetValue((nint)original, out var face))
            return face;
        var name = this.presetFaces.Count != 0 ? GameFontNames.GetFaceName(original) : null;
        face = name is not null && this.presetFaces.TryGetValue(name, out var found) ? found : this.gameFace;
        this.faceByFont[(nint)original] = face;
        return face;
    }

    private void DisposePresetFaces()
    {
        foreach (var face in this.presetFaces.Values)
            face.Dispose();
        this.presetFaces.Clear();
        this.gameFace?.Dispose();
    }

    /// <summary>
    /// Gets the glyphs of a face at a size, made with the metrics they have for a game font. They are shared by the fonts
    /// of one name (a font and its lobby version), and go to the atlas of its family.
    /// </summary>
    private SizedFont GetSized(GameFont* original, int halfPx)
    {
        var face = this.GetFace(original);
        var fontName = GameFontNames.GetFaceName(original) ?? $"0x{(nint)original:X}";
        if (!this.sizedFonts.TryGetValue((face, halfPx, fontName), out var sized))
        {
            var px = halfPx / 2f;
            var (ascent, lineHeight) = face.GetLineMetrics(px, original);
            sized = new(face, px, ascent, lineHeight, (nint)original, this.GetAtlas(Preset.FamilyOf(fontName)));
            this.sizedFonts.Add((face, halfPx, fontName), sized);
        }

        return sized;
    }

    /// <summary>Gets the atlas of a game font family, made on its first use.</summary>
    private GlyphAtlas GetAtlas(string family)
    {
        if (this.atlases.TryGetValue(family, out var atlas))
            return atlas;

        atlas = new(string.Equals(family, "AXIS", StringComparison.OrdinalIgnoreCase) ? AxisAtlasSize : OtherAtlasSize, family);
        atlas.PageAdded += () => this.OnPageAdded(atlas);
        this.atlases.Add(family, atlas);

        // Glyphs refer to page texture indices, and the renderer only has vertex buffers for those below a font's texture
        // count: the first page is there before any glyph.
        atlas.EnsurePage();
        return atlas;
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
            Host.Log.Error(ex, "Using the plugin's edge shader failed; edges are widened with the game's");
            this.edgeShader.Set(false);
        }

        // Copies claim their texture width by the edge.
        foreach (var (copy, info) in this.copies)
            this.Sync((GameFont*)copy, info);
        this.RebuildGlyphs();
    }

    private readonly EdgeShader edgeShader = new();

    /// <summary>
    /// Makes a glyph rasterized for a size (and adjusted as its element says) ready to be placed: surrounded with empty
    /// pixels for a wide edge, which is only drawn within a glyph's box and the pixel around it.
    /// </summary>
    internal RasterGlyph FinishGlyph(SizedFont sized, RasterGlyph r)
    {
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
    /// Forgets every glyph and empties the atlases, so they are rasterized again with the current settings. Framework
    /// thread, between frames. Nameplates already baked keep their pixels until the game bakes them again.
    /// </summary>
    public void RebuildGlyphs()
    {
        this.shaper.Clear();
        this.fullAtlases.Clear();
        this.gameReads.Clear();
        foreach (var info in this.copies.Values)
            info.GameGlyphs.Clear();
        foreach (var sized in this.sizedFonts.Values)
            sized.Glyphs.Clear();
        this.FreeGlyphs();
        foreach (var atlas in this.atlases.Values)
            atlas.Clear();
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
        this.edgeShader.Dispose();
        this.buildFontHook?.Dispose();
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
        this.FreeGlyphs();
        if (this.emptyGlyph is not null)
        {
            NativeMemory.Free(this.emptyGlyph);
            this.emptyGlyph = null;
        }

        foreach (var atlas in this.atlases.Values)
            atlas.Dispose();
        this.atlases.Clear();
        this.gameTextures.Dispose();
        this.DisposePresetFaces();
        this.builtInFace?.Dispose();
        this.rasterizer?.Dispose();
    }

    /// <summary>Makes every font set pick its font again on its next use.</summary>
    private static void ForgetPickedFonts()
    {
        var manager = GameFontManager.Instance();
        if (manager is null || manager->FontSets is null)
            return;
        for (var i = 0; i < GameFontManager.FontSetCount; i++)
        {
            var set = manager->FontSet(i);
            set->CurrentFont = null;
            set->CurrentFontSize = 0;
        }
    }

    /// <summary>
    /// A font cache keeps the glyph pointers of the text as laid out when it was built, at the node's unscaled size
    /// (FUN_140665600(node, false)), and draws use them while picking the font for the on-screen size. Glyphs of a copy
    /// only fit the copy of their size, and a copy's glyphs must not outlive the plugin, so while the replacement is on
    /// nodes get no cache: their text is laid out at each draw, as for nodes that never asked for one.
    /// </summary>
    private void BuildFontCacheDetour(nint node)
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
        foreach (var uld in GameUi.GetLoadedUnitUlds())
            this.UpdateFontCaches(uld, 0);
    }

    private void UpdateFontCaches(nint uld, int depth)
    {
        if (depth > 16)
            return;
        var count = GameUi.GetNodeCount(uld);
        for (var i = 0; i < count; i++)
        {
            var node = GameUi.GetNode(uld, i);
            if (node == 0)
                continue;
            var type = GameUi.GetNodeType(node);
            if (type == GameUi.TextNodeType)
            {
                if (this.enabled)
                {
                    if (*(ushort*)(node + this.fontCacheSlotOffset) != 0)
                        this.freeFontCache(node);
                }
                else if ((*(byte*)(node + this.fontCacheFlagsOffset) & this.useFontCacheFlag) != 0)
                {
                    this.buildFontCacheHook.Original(node);
                }
            }
            else if (type >= GameUi.FirstComponentNodeType)
            {
                var componentUld = GameUi.GetComponentUld(node);
                if (componentUld != 0)
                    this.UpdateFontCaches(componentUld, depth + 1);
            }
        }
    }

    /// <summary>
    /// The game built a font again in place (a reload, a switch between the lobby's fonts and the game's), freeing the
    /// glyph map and glyphs its copies borrow: they take them again, with the face of the font's name now.
    /// </summary>
    private int BuildFontDetour(nint manager, ushort index)
    {
        var result = this.buildFontHook.Original(manager, index);
        try
        {
            var fonts = (GameFontManager*)manager;
            if (index < fonts->FontCount)
                this.OnFontBuilt(fonts->Font(index));
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Refreshing the copies of a rebuilt font failed; disabling");
            this.enabled = false;
            ForgetPickedFonts();
        }

        return result;
    }

    private void OnFontBuilt(GameFont* font)
    {
        this.faceByFont.Remove((nint)font);
        var any = false;
        foreach (var (copy, info) in this.copies)
        {
            if (info.Original != (nint)font)
                continue;
            info.Sized = this.GetSized(font, info.HalfPx);
            info.GameGlyphs.Clear();
            this.Sync((GameFont*)copy, info);
            any = true;
        }

        if (any)
            this.TextInvalidated?.Invoke();
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

        // A font not built yet (its glyph map may be gone), or one using the texture indices the atlas pages take, is left
        // alone.
        if (!font->IsReady || font->TextureCount > GlyphAtlas.FirstTextureIndex)
            return font;

        try
        {
            // Picked for the on-screen size, even where the game picked for another (the unscaled size of a node with a
            // fixed font resolution, or a pick scale of its own), so the renderer doesn't scale the glyphs.
            var size = set->ScaledSizeY;
            var halfPx = Math.Clamp((int)Rounding.Round(size * 2), MinHalfPx, MaxHalfPx);
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
            Host.Log.Error(ex, "Replacing a font failed; disabling");
            this.enabled = false;
            ForgetPickedFonts();
            return font;
        }
    }

    private nint LayOutCharacterDetour(nint analyzer, byte** text, nint state)
    {
        var outer = current;
        var vtbl = *(nint*)analyzer;
        current = new()
        {
            Character = (nint)(*text),
            MeasuringOnly = vtbl != this.rendererVtbl && vtbl != this.renderCountVtbl,
            State = state,
        };
        try
        {
            return this.layOutCharacterHook.Original(analyzer, text, state);
        }
        finally
        {
            // The italic macro reads the bit when italics end (the glyph after moves by the font's italic correction).
            if (current.ItalicCleared)
                *(uint*)(state + this.stateFlagsOffset) |= this.stateItalicFlag;
            current = outer;
        }
    }

    /// <summary>
    /// Gets how the character being laid out is in italics: by an italic macro (which measuring sees too), or by its text
    /// node (which only drawing sees: the bit is in the font set's own flags, before any macro changed the copy).
    /// </summary>
    private ItalicMode GetItalicMode(GameFontSet* set)
    {
        if (this.stateFlagsOffset < 0 || current.State == 0 || (*(uint*)(current.State + this.stateFlagsOffset) & this.stateItalicFlag) == 0)
            return ItalicMode.Upright;
        return set is not null && this.setFlagsOffset >= 0 && (*(uint*)((byte*)set + this.setFlagsOffset) & this.setItalicFlag) != 0
                   ? ItalicMode.Images
                   : ItalicMode.Real;
    }

    /// <summary>Marks a cell as a real italic glyph, which the game must not shear.</summary>
    internal void MarkRealItalic(nint cell) => this.CellOf((GameGlyph*)cell).RealItalic = true;

    /// <summary>
    /// Gets what a character a copy has no glyph for is laid out as. FUN_1406EEC70 takes the geta mark (U+3013), else
    /// '-', else U+3400, from the font's glyph map itself rather than through GetGlyph, which would give the game font's
    /// glyph, at its size and with the edge of its texture. The copy's own glyphs of them are given instead.
    /// </summary>
    private GameGlyph* GetMissingGlyph(GameFontSet* set, uint utf8Value, GameFont* font)
    {
        foreach (var substitute in this.missingGlyphSubstitutes)
        {
            if (utf8Value == (uint)substitute)
                return null;
            var glyph = this.GetGlyphDetour(set, (uint)substitute, font);
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
        var p = (byte*)current.Character;
        if (p is not null && GameUtf8.PackSequence(p, GameUtf8.SequenceLength(*p)) == utf8Value)
        {
            try
            {
                var italic = this.GetItalicMode(set);
                var shaped = (GameGlyph*)this.shaper.TryGetGlyph(sized, p, (nint)this.emptyGlyph, italic);
                if (shaped is not null)
                {
                    // A real italic glyph is drawn as it is: the italic bit is cleared while the character is laid out
                    // (the quad emitter runs after this lookup), and set again after (LayOutCharacterDetour).
                    if (italic != ItalicMode.Upright && ((GlyphSlot*)shaped)->Id >= 0 && this.CellOf(shaped).RealItalic)
                    {
                        *(uint*)(current.State + this.stateFlagsOffset) &= ~this.stateItalicFlag;
                        current.ItalicCleared = true;
                    }

                    return this.WithPixels(shaped);
                }
            }
            catch (Exception ex)
            {
                Host.Log.Error(ex, "Shaping failed; the character is laid out by itself");
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
                Host.Log.Error(ex, "Rasterizing 0x{value:X} at {px} px failed", utf8Value, sized.Px);
                glyph = 0;
            }

            sized.Glyphs.Add(utf8Value, glyph);
        }

        if (glyph != 0)
            return this.WithPixels((GameGlyph*)glyph);

        // The game's glyph, looked up in the game font, whose glyph map the copy borrows.
        var game = this.getGlyphHook.Original(set, utf8Value, (GameFont*)info.Original);
        if (game is null)
            return current.Character != 0 ? this.GetMissingGlyph(set, utf8Value, font) : null;

        try
        {
            var own = this.GetGameGlyph(info, game, utf8Value);
            return own is null ? game : this.WithPixels(own);
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
            return game;
        }
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph, scaled to the copy's size: drawn from the game's texture, the edge
    /// would step by texels of the atlas's width (all of a font's textures share one), not the game texture's. Its pixels
    /// are read from the game's texture at the next upload. Null to draw the game's glyph as it is.
    /// </summary>
    private GameGlyph* GetGameGlyph(CopyInfo info, GameGlyph* game, uint utf8Value)
    {
        if (info.GameGlyphs.TryGetValue(utf8Value, out var made))
            return (GameGlyph*)made;

        var original = (GameFont*)info.Original;
        var textureIndex = game->TextureIndex;
        var texture = textureIndex < GlyphAtlas.FirstTextureIndex ? original->GetTexture(textureIndex) : 0;
        GameGlyph* glyph = null;
        if (texture != 0 && GameTextureReader.CanRead(texture))
        {
            var sized = info.Sized;
            var scale = sized.Px / original->Size;
            var top = game->OffsetY - original->Ascent;
            var (left, t, w, h) = RasterGlyph.ScaledBounds(0, top, game->Width, game->Height, scale, 0, 0);
            var r = new RasterGlyph((int)Rounding.Round((game->Width + game->OffsetX) * scale), left, t, w, h, new byte[w * h]);
            var source = new GameTextureSource(texture, game->X, game->Y, game->Width, game->Height, game->Channel, 0, top, scale);
            glyph = this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value, source);
        }

        info.GameGlyphs[utf8Value] = (nint)glyph;
        return glyph;
    }

    /// <summary>
    /// Fills the cells of game glyphs from the game's textures, then copies the atlas's changes to the GPU. On the thread
    /// that calls Present, once a frame.
    /// </summary>
    public void Upload()
    {
        this.frameTick = Environment.TickCount64;
        foreach (var g in this.gameReads)
        {
            var glyph = (GameGlyph*)g;
            ref var cell = ref this.CellOf(glyph);
            try
            {
                var s = cell.Source!;
                var pixels = this.gameTextures.Read(s.Texture, s.X, s.Y, s.Width, s.Height, s.Plane);
                cell.Raster = WithSourcePixels(cell.Raster, s, pixels);
                WriteCell(glyph, cell);
            }
            catch (Exception ex)
            {
                Host.Log.Error(ex, "Reading a game glyph's pixels failed");
            }

            cell.Raster = default;
            cell.Source = null;
        }

        this.gameReads.Clear();

        foreach (var atlas in this.fullAtlases.ToArray())
        {
            if (Environment.TickCount64 - this.lastEviction.GetValueOrDefault(atlas) < EvictIntervalMs)
                continue;
            try
            {
                this.EvictPlane(atlas);
            }
            catch (Exception ex)
            {
                Host.Log.Error(ex, "Making room in the {family} glyph atlas failed; making every glyph again", atlas.Name);
                this.RebuildGlyphs();
                break;
            }
        }

        foreach (var atlas in this.atlases.Values)
            atlas.Upload();
    }

    /// <summary>Gets a glyph's box filled with its game texture's pixels (read as 8-bit coverage), scaled.</summary>
    private static RasterGlyph WithSourcePixels(RasterGlyph box, GameTextureSource s, byte[] sourceAlpha)
    {
        var scaled = new RasterGlyph(0, s.Left, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[box.Width * box.Height];
        RasterGlyph.BlitMax(alpha, box.Width, box.Height, scaled, scaled.Left - box.Left, scaled.Top - box.Top);
        return box with { Alpha = alpha };
    }

    /// <summary>
    /// Takes what a copy borrows from its game font (glyph map, glyphs, textures, flags), keeping its own size, metrics
    /// and pages. When the game builds the font again in place (BuildFont), what was borrowed is freed, and taken again.
    /// </summary>
    private void Sync(GameFont* copy, CopyInfo info)
    {
        var sized = info.Sized;
        copy->CopyFrom((GameFont*)info.Original);
        copy->Size = sized.Px;
        copy->Ascent = sized.Ascent;
        copy->LineHeight = sized.LineHeight;

        // Neither the narrower-glyph substitution nor the game's kerning pairs apply to these glyphs.
        copy->Secondary = null;
        copy->SecondaryRatio = 0;
        copy->KerningCount = 0;

        // Real italics are spaced by shaping: no gap after them for sheared glyphs leaning past their advance. Measuring
        // reads it from the copy too, so it stays as drawn.
        copy->ItalicCorrection = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex): the pages', which the
        // game font's textures may not share (the lobby fonts' are smaller), else outlines sample several texels apart.
        // Claiming a narrower width makes the step, and so the edge, that many times wider; the plugin's edge shader takes
        // the step as its radius.
        var width = (ushort)Math.Clamp(Rounding.Round(sized.Atlas.Size / this.Edge.GetWidth(sized.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;
        ApplyPages(copy, sized.Atlas);
    }

    private GameFont* GetOrCreateCopy(GameFont* original, int halfPx)
    {
        if (this.copiesByKey.TryGetValue(((nint)original, halfPx), out var existing))
            return (GameFont*)existing;

        var copy = (GameFont*)NativeMemory.Alloc((nuint)GameFont.StructSize);
        var info = new CopyInfo((nint)original, halfPx, this.GetSized(original, halfPx));
        this.Sync(copy, info);
        this.copies.Add((nint)copy, info);
        this.copiesByKey.Add(((nint)original, halfPx), (nint)copy);
        return copy;
    }

    /// <summary>Gives a copy the pages of its family's atlas, after the game font's textures.</summary>
    private static void ApplyPages(GameFont* copy, GlyphAtlas atlas)
    {
        for (var i = 0; i < atlas.PageCount; i++)
            copy->SetTexture(GlyphAtlas.FirstTextureIndex + i, atlas.GetKernelTexture(i));
        copy->TextureCount = (ushort)(GlyphAtlas.FirstTextureIndex + atlas.PageCount);
    }

    private void OnPageAdded(GlyphAtlas atlas)
    {
        foreach (var (copy, info) in this.copies)
        {
            if (info.Sized.Atlas == atlas)
                ApplyPages((GameFont*)copy, atlas);
        }
    }

    /// <summary>Rasterizes a glyph into the atlas; null to leave it to the game.</summary>
    private GameGlyph* CreateGlyph(SizedFont sized, uint utf8Value)
    {
        var codepoint = GameUtf8.Unpack(utf8Value);

        // A broken sequence, or a codepoint the face leaves to the game, stays the game's.
        if (codepoint < 0)
            return null;
        return sized.Face.TryRasterize(codepoint, sized.Px, (GameFont*)sized.GameFont, out var r)
            ? this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value)
            : null;
    }

    /// <summary>
    /// Puts a rasterized glyph or cluster into the atlas as a game glyph advancing by <see cref="RasterGlyph.Advance"/>.
    /// Its pixels go to the atlas when it is first drawn; for a glyph made from a game glyph, they are read from
    /// <paramref name="source"/> then (<paramref name="r"/> is blank).
    /// </summary>
    internal GameGlyph* PlaceCell(SizedFont sized, RasterGlyph r, uint utf8Value, GameTextureSource? source = null)
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

        // Text is measured far more than drawn (every text change, at the node's unscaled size too): a glyph keeps its
        // pixels here until it is drawn (WithPixels, which every glyph handed to the game goes through).
        if (width != 0)
        {
            this.CellOf(glyph) = new()
            {
                State = CellState.Pending,
                Atlas = sized.Atlas,
                Raster = r,
                BoxLeft = boxLeft,
                InkTop = inkTop - top,
                Source = source,
            };
        }

        return glyph;
    }

    /// <summary>
    /// Puts a glyph's pixels into the atlas if they aren't yet, for a glyph about to be drawn, and notes that it was drawn.
    /// A glyph that finds no room draws nothing this time (the same advance), and room is made at the next upload (EvictPlane).
    /// </summary>
    private GameGlyph* WithPixels(GameGlyph* glyph)
    {
        if (current.MeasuringOnly || ((GlyphSlot*)glyph)->Id < 0)
            return glyph;

        ref var cell = ref this.CellOf(glyph);
        if (cell.State == CellState.Placed)
        {
            cell.Drawn = this.frameTick;
            return glyph;
        }

        if (cell.State != CellState.Pending)
            return glyph;
        try
        {
            if (this.WritePixels(glyph, ref cell, this.frameTick))
                return glyph;
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Placing a glyph in the atlas failed");
            cell.State = CellState.None;
            cell.Raster = default;
            cell.Source = null;
            DropPixels(glyph);
            return glyph;
        }

        this.fullAtlases.Add(cell.Atlas!);
        return this.GetBlank(glyph);
    }

    /// <summary>
    /// Places a pending glyph's pixels in its atlas, drawn last at <paramref name="drawn"/>; false if there is no room. A
    /// game glyph's pixels are read at the next upload.
    /// </summary>
    private bool WritePixels(GameGlyph* glyph, ref Cell cell, long drawn)
    {
        if (!cell.Atlas!.TryAllocate(glyph->Width, glyph->Height, out var page, out var plane, out var x, out var y))
            return false;

        glyph->Packed = GameGlyph.Pack(x, y, plane, GlyphAtlas.FirstTextureIndex + page);
        cell.State = CellState.Placed;
        cell.Drawn = drawn;
        if (cell.Source is not null)
        {
            this.gameReads.Add((nint)glyph);
        }
        else
        {
            WriteCell(glyph, cell);
            cell.Raster = default;
        }

        return true;
    }

    /// <summary>Gets a glyph that advances as a glyph does but draws nothing, for one whose pixels found no room yet.</summary>
    private GameGlyph* GetBlank(GameGlyph* glyph)
    {
        if (this.CellOf(glyph).Blank is not 0 and var blank)
            return (GameGlyph*)blank;
        var b = this.AllocateGlyph();
        *b = *glyph;
        DropPixels(b);
        this.CellOf(glyph).Blank = (nint)b;
        return b;
    }

    /// <summary>
    /// Makes room in a full atlas by emptying one of its planes: the one whose glyphs not drawn in the last
    /// <see cref="KeepDrawnMs"/> take the most room. Its glyphs drawn since are placed in it again (the most recently drawn
    /// first), from their pixels; the others leave the atlas, keeping their pixels to be placed again when they are next
    /// drawn. If every glyph was drawn recently, the plane's least recently drawn leave until half of it is free. On the
    /// thread that calls Present, after this frame's text was laid out: the next frame lays out with the new places, and
    /// the upload that follows puts them in the texture.
    /// </summary>
    private void EvictPlane(GlyphAtlas atlas)
    {
        var now = Environment.TickCount64;
        var stale = new long[GlyphAtlas.MaxPlanes];
        var oldest = new long[GlyphAtlas.MaxPlanes];
        Array.Fill(oldest, long.MaxValue);
        var all = CollectionsMarshal.AsSpan(this.cells);
        for (var id = 0; id < all.Length; id++)
        {
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas)
                continue;
            var glyph = &this.SlotAt(id)->Glyph;
            var plane = PlaneOf(glyph);
            if (now - all[id].Drawn > KeepDrawnMs)
                stale[plane] += GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            oldest[plane] = Math.Min(oldest[plane], all[id].Drawn);
        }

        // The most stale room; without any, the plane whose glyphs were drawn least recently.
        var victim = Array.IndexOf(stale, stale.Max());
        if (stale[victim] == 0)
            victim = Array.IndexOf(oldest, oldest.Min());

        var moved = new List<(int Id, long Drawn, byte[] Pixels)>();
        for (var id = 0; id < all.Length; id++)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas || PlaneOf(glyph) != victim)
                continue;
            var pixels = atlas.Read(glyph->TextureIndex - GlyphAtlas.FirstTextureIndex, glyph->Channel, glyph->X, glyph->Y, glyph->Width, glyph->Height);
            moved.Add((id, all[id].Drawn, pixels));
        }

        moved.Sort((a, b) => b.Drawn.CompareTo(a.Drawn));
        atlas.ClearPlane(victim / GlyphAtlas.PlanesPerPage, victim % GlyphAtlas.PlanesPerPage);

        // What may stay: the glyphs drawn recently, but no more than half of the plane if none were stale.
        var budget = stale[victim] == 0 ? (long)atlas.Size * atlas.Size / 2 : long.MaxValue;
        var kept = 0;
        foreach (var (id, drawn, pixels) in moved)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            ref var cell = ref all[id];
            cell.State = CellState.Pending;
            cell.Raster = new(0, 0, 0, glyph->Width, glyph->Height, pixels);
            cell.BoxLeft = 0;
            cell.InkTop = 0;
            glyph->Packed = GameGlyph.Pack(0, 0, 0, GlyphAtlas.FirstTextureIndex);
            var area = GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            if (now - drawn <= KeepDrawnMs && area <= budget && this.WritePixels(glyph, ref cell, drawn))
            {
                budget -= area;
                kept++;
            }
        }

        this.fullAtlases.Remove(atlas);
        this.lastEviction[atlas] = now;
        Host.Log.Information(
            "The {family} glyph atlas was full: emptied plane {plane}, kept {kept} recently drawn glyphs of its {count}",
            atlas.Name,
            victim,
            kept,
            moved.Count);
    }

    /// <summary>Gets the atlas plane of a placed glyph (page * planes per page + channel).</summary>
    private static int PlaneOf(GameGlyph* glyph) =>
        ((glyph->TextureIndex - GlyphAtlas.FirstTextureIndex) * GlyphAtlas.PlanesPerPage) + glyph->Channel;

    /// <summary>Writes a placed glyph's pixels to its place in the atlas.</summary>
    private static void WriteCell(GameGlyph* glyph, in Cell cell)
    {
        var r = cell.Raster;

        // The ink's rows in the box (cut off only past the game's byte-sized fields).
        var firstRow = Math.Max(0, -cell.InkTop);
        var lastRow = Math.Min(r.Height, glyph->Height - cell.InkTop);
        if (lastRow > firstRow)
        {
            cell.Atlas!.Write(
                glyph->TextureIndex - GlyphAtlas.FirstTextureIndex,
                glyph->Channel,
                glyph->X,
                glyph->Y + cell.InkTop + firstRow,
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

    private GameGlyph* AllocateGlyph()
    {
        var id = this.cells.Count;
        if (id % SlotBlockSize == 0)
            this.slotBlocks.Add((nint)NativeMemory.AllocZeroed((nuint)(SlotBlockSize * sizeof(GlyphSlot))));
        var slot = this.SlotAt(id);
        slot->Id = id;
        this.cells.Add(default);
        return &slot->Glyph;
    }

    private GlyphSlot* SlotAt(int id) => (GlyphSlot*)this.slotBlocks[id / SlotBlockSize] + (id % SlotBlockSize);

    /// <summary>Gets what is kept of a glyph made here. The reference is good until the next glyph is made.</summary>
    private ref Cell CellOf(GameGlyph* glyph) => ref CollectionsMarshal.AsSpan(this.cells)[((GlyphSlot*)glyph)->Id];

    private void FreeGlyphs()
    {
        foreach (var block in this.slotBlocks)
            NativeMemory.Free((void*)block);
        this.slotBlocks.Clear();
        this.cells.Clear();
    }

    /// <summary>
    /// A glyph made here, where the game reads it (it must not move): the glyph, then the id of what is kept of it
    /// (<see cref="Cell"/>), -1 for the empty glyph. Glyph pointers handed to the game are of these.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphSlot
    {
        public GameGlyph Glyph;
        public int Id;
    }

    private enum CellState : byte
    {
        /// <summary>No pixels: an empty glyph, a blank, or one whose pixels couldn't be placed.</summary>
        None,

        /// <summary>Pixels waiting to go to the atlas when the glyph is next drawn.</summary>
        Pending,

        /// <summary>In the atlas, where <see cref="GameGlyph.Packed"/> says.</summary>
        Placed,
    }

    /// <summary>What is kept of a glyph made here, by its slot's id.</summary>
    private struct Cell
    {
        public CellState State;

        /// <summary>The atlas its pixels go to.</summary>
        public GlyphAtlas? Atlas;

        /// <summary>
        /// Its coverage while pending (and for a game glyph, until its pixels are read), with the ink's left in the box and
        /// its top row in the box.
        /// </summary>
        public RasterGlyph Raster;

        public int BoxLeft;

        public int InkTop;

        /// <summary>For a glyph made from a game glyph: where its pixels are read from.</summary>
        public GameTextureSource? Source;

        /// <summary>When it was last drawn (Environment.TickCount64 of the frame), once placed.</summary>
        public long Drawn;

        /// <summary>The glyph drawn instead while its pixels find no room; 0 until one is needed.</summary>
        public nint Blank;

        /// <summary>Whether it is a real italic glyph, which the game must not shear.</summary>
        public bool RealItalic;
    }

    /// <summary>
    /// A game glyph's pixels: a rectangle of a plane of a game font texture (a Kernel::Texture), whose top left is at
    /// (<see cref="Left"/>, <see cref="Top"/>) from the pen in the game font's pixels, scaled by <see cref="Scale"/>.
    /// </summary>
    internal sealed record GameTextureSource(nint Texture, int X, int Y, int Width, int Height, int Plane, int Left, int Top, float Scale);

    /// <summary>
    /// A copy of a game font at a size in half pixels: its game font, the glyphs of its face at its size, and the glyphs
    /// made from the game font's glyphs, by packed UTF-8 value (0 for one drawn as it is).
    /// </summary>
    private sealed class CopyInfo(nint original, int halfPx, SizedFont sized)
    {
        public nint Original { get; } = original;

        public int HalfPx { get; } = halfPx;

        public SizedFont Sized { get; set; } = sized;

        public Dictionary<uint, nint> GameGlyphs { get; } = [];
    }

    /// <summary>
    /// The character the game is laying out on a thread, for GetGlyph to look up in its shaped run: whether it is laid out
    /// by an analyzer that only measures (its glyph needs no pixels yet), its layout state (whose flags have italics), and
    /// whether its italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
    /// </summary>
    private struct LayoutContext
    {
        public nint Character;
        public bool MeasuringOnly;
        public nint State;
        public bool ItalicCleared;
    }

    /// <summary>
    /// The glyphs of a face at one pixel size, shared by the copies of the game fonts of that face at that size (a font
    /// and its lobby version). <see cref="GameFont"/> is the first of them, for the game's glyphs and metrics.
    /// </summary>
    internal sealed class SizedFont(ReplacementFace face, float px, int ascent, int lineHeight, nint gameFont, GlyphAtlas atlas)
    {
        public ReplacementFace Face { get; } = face;

        /// <summary>Gets the atlas of the font's family, which its glyphs go to.</summary>
        public GlyphAtlas Atlas { get; } = atlas;

        public nint GameFont { get; } = gameFont;

        public float Px { get; } = px;

        public int Ascent { get; } = ascent;

        public int LineHeight { get; } = lineHeight;

        /// <summary>By packed UTF-8 value; 0 for a glyph left to the game.</summary>
        public Dictionary<uint, nint> Glyphs { get; } = [];
    }
}
