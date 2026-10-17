using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Dalamud.Interface.GameFonts;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Records;

using Lumina.Data.Files;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

/// <summary>
/// A game font's glyphs read from the game's files (xivres fontdata_fixed_size_font, as FontChanger's elements of game
/// fonts draw them): of the face of the family the element's lookup names whose size is closest to the element's, drawn
/// at the element's size as if it were the face's. Glyph IDs are codepoints.
/// </summary>
internal sealed class GameFontDataFaceSource : NominalFaceSource
{
    // The game's font textures (B8G8R8A8, with their width and height) by path, shared by the fonts drawn from them; null
    // for those that can't be read.
    private static readonly Dictionary<string, (byte[] Pixels, int Width, int Height)?> Textures = [];

    private readonly FdtReader _fdt;
    private readonly string _textures;
    private readonly float _size;

    private GameFontDataFaceSource(FdtReader fdt, string textures, float size)
    {
        this._fdt = fdt;
        this._textures = textures;
        this._size = size;
    }

    public override IEnumerable<Rune> Codepoints => this._fdt.Glyphs.Where(g => Rune.IsValid(g.CharInt)).Select(g => new Rune(g.CharInt));

    /// <summary>Reads the face of an element's game font family closest to its size; null if there is none, or it can't be read.</summary>
    public static GameFontDataFaceSource? Create(FaceElement def, string faceName)
    {
        // On a tie, the smaller: faces are listed smallest first.
        var face = GameFonts.FacesOf(def.Lookup.Name).MinBy(f => MathF.Abs(f.Size - def.Size));
        if (face is null)
        {
            Plugin.Log.Warning("{face}: {family} isn't a game font family", faceName, def.Lookup.Name);
            return null;
        }

        try
        {
            var file = Plugin.DataManager.GetFile($"common/font/{face.Name}.fdt") ?? throw new InvalidOperationException("The file isn't in the game's data.");
            return new(new(file.Data), face.Textures, def.Size);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "{face}: the game font {name} can't be read", faceName, face.Name);
            return null;
        }
    }

    public override uint GetGlyphId(Rune rune) => this._fdt.FindGlyph(rune.Value) is not null ? (uint)rune.Value : 0;

    public override float GetAdvance(float px, uint glyphId) => this._fdt.FindGlyph((int)glyphId) is { } g ? g.AdvanceWidth * this.GetScale(px) : 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px)
    {
        var scale = this.GetScale(px);
        var header = this._fdt.FontHeader;
        return ((int)Rounding.Round(header.Ascent * scale), (int)Rounding.Round(header.LineHeight * scale));
    }

    public override RasterGlyph? RasterizeGlyph(float px, uint glyphId)
    {
        if (this._fdt.FindGlyph((int)glyphId) is not { } g ||
            GetTexture(this._textures.Replace("{}", (g.TextureFileIndex + 1).ToString())) is not (var pixels, var width, var height))
            return null;

        var alpha = new byte[g.BoundingWidth * g.BoundingHeight];
        for (var y = 0; y < g.BoundingHeight && g.TextureOffsetY + y < height; y++)
        {
            for (var x = 0; x < g.BoundingWidth && g.TextureOffsetX + x < width; x++)
                alpha[(y * g.BoundingWidth) + x] = pixels[((((g.TextureOffsetY + y) * width) + g.TextureOffsetX + x) * 4) + g.TextureChannelByteIndex];
        }

        var glyph = new RasterGlyph(g.AdvanceWidth, 0, g.CurrentOffsetY - this._fdt.FontHeader.Ascent, g.BoundingWidth, g.BoundingHeight, alpha);
        var scale = this.GetScale(px);
        return scale == 1 ? glyph : glyph.Scaled(scale, 0, 0) with { Advance = (int)Rounding.Round(g.AdvanceWidth * scale) };
    }

    /// <summary>Gets the scale from the face's glyphs to a size of the element: 1 within the half-pixel rounding of copy sizes.</summary>
    private float GetScale(float px)
    {
        var scale = this._size > 0 ? px / this._size : 1;
        var size = this._fdt.FontHeader.Size;
        return MathF.Abs((scale * size) - size) <= 0.25f ? 1 : scale;
    }

    private static (byte[] Pixels, int Width, int Height)? GetTexture(string path)
    {
        lock (Textures)
        {
            if (Textures.TryGetValue(path, out var texture))
                return texture;
            try
            {
                texture = Plugin.DataManager.GetFile<TexFile>(path) is { } file ? (file.ImageData, file.Header.Width, file.Header.Height) : null;
                if (texture is null)
                    Plugin.Log.Warning("The game font texture {path} isn't in the game's data", path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "The game font texture {path} can't be read", path);
            }

            Textures.Add(path, texture);
            return texture;
        }
    }
}
