using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Dalamud.Interface.GameFonts;

using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using Lumina.Data.Files;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;

internal sealed class GameFontDataFaceSource : NominalFaceSource
{
    private static readonly Dictionary<string, (byte[] Pixels, int Width, int Height)?> Textures = [];
    private static int _instances;

    private readonly FdtReader _fdt;
    private readonly string _textures;
    private readonly float _size;

    private GameFontDataFaceSource(FdtReader fdt, string textures, float size)
    {
        this._fdt = fdt;
        this._textures = textures;
        this._size = size;
        lock (Textures)
            _instances++;
    }

    public override IEnumerable<Rune> Codepoints => this._fdt.Glyphs.Where(static g => Rune.IsValid(g.CharInt)).Select(static g => new Rune(g.CharInt));

    public static GameFontDataFaceSource? Create(FontElement def, string fontName)
    {
        // On a tie, the smaller: fonts are listed smallest first.
        var gameFont = GameFonts.FontsOf(def.Lookup.FamilyName).MinBy(f => MathF.Abs(f.Size - def.Size));
        if (gameFont is null)
        {
            Plugin.Log.Warning("{font}: {family} isn't a game font family", fontName, def.Lookup.FamilyName);
            return null;
        }

        try
        {
            var file = Plugin.DataManager.GetFile($"common/font/{gameFont.Name}.fdt") ?? throw new InvalidOperationException("The file isn't in the game's data.");
            return new(new(file.Data), gameFont.Textures, def.Size);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "{font}: the game font {name} can't be read", fontName, gameFont.Name);
            return null;
        }
    }

    public override uint GetGlyphId(Rune rune) => this._fdt.FindGameGlyph(rune.Value) is not null ? (uint)rune.Value : 0;

    public override float GetAdvance(float px, uint glyphId) => this._fdt.FindGameGlyph((int)glyphId) is { } g ? g.AdvanceWidth * this.GetScale(px) : 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px)
    {
        var scale = this.GetScale(px);
        var header = this._fdt.FontHeader;
        return ((int)Misc.StdRound(header.Ascent * scale), (int)Misc.StdRound(header.LineHeight * scale));
    }

    public override void Dispose()
    {
        lock (Textures)
        {
            if (--_instances == 0)
                Textures.Clear();
        }
    }

    protected override RasterGlyph? RasterizeGlyph(float px, uint glyphId)
    {
        if (this._fdt.FindGameGlyph((int)glyphId) is not { } g ||
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
        return scale == 1 ? glyph : glyph.Scaled(scale, 0, 0) with { Advance = (int)Misc.StdRound(g.AdvanceWidth * scale) };
    }

    // 1 within the half-pixel rounding of copy sizes, so that those draw the font's own glyphs.
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
                texture = Plugin.DataManager.GetFile<TexFile>(path) is { } file
                              ? (file.ImageData, file.Header.Width, file.Header.Height)
                              : throw new FileNotFoundException();
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
