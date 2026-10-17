using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Dalamud.Interface.GameFonts;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Glyph boxes are those of the glyphs' images, which the game draws at the pen.</summary>
internal sealed class GameFontProbe : IFontProbe
{
    private readonly FdtReader _fdt;
    private readonly float _size;

    private GameFontProbe(FdtReader fdt)
    {
        this._fdt = fdt;
        this._size = fdt.FontHeader.Size > 0 ? fdt.FontHeader.Size : 1;
    }

    public float Ascent => this._fdt.FontHeader.Ascent / this._size;
    public float LineHeight => this._fdt.FontHeader.LineHeight / this._size;
    public IEnumerable<Rune> Codepoints => this._fdt.Glyphs.Where(static g => Rune.IsValid(g.CharInt)).Select(static g => new Rune(g.CharInt));

    public static GameFontProbe? Open(string fontName)
    {
        try
        {
            return Plugin.DataManager.GetFile($"common/font/{fontName}.fdt") is { } file ? new(new(file.Data)) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "The game font {name} can't be read", fontName);
            return null;
        }
    }

    public bool TryGetGlyph(Rune rune, out GlyphBox box)
    {
        if (this._fdt.FindGameGlyph(rune.Value) is not { } g)
        {
            box = default;
            return false;
        }

        var top = this._fdt.FontHeader.Ascent - g.CurrentOffsetY;
        box = new(0, (g.AdvanceWidth - g.BoundingWidth) / this._size, g.AdvanceWidth / this._size, top / this._size, (top - g.BoundingHeight) / this._size);
        return true;
    }
}
