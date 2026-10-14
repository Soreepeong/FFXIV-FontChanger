using System.Text;

using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public interface IFontProbe
{
    bool TryGetGlyph(Rune rune, out GlyphBox box);
    GlyphBox GetGlyphOrDefault(Rune rune) => this.TryGetGlyph(rune, out var box) ? box : default;
    bool HasBitmapOf(int ppem);
    bool HasGposFeature(string tag);
    bool HasGsubFeature(string tag);
}
