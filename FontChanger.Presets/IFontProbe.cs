using System.Collections.Generic;
using System.Text;

using FontChanger.Presets.Records;

namespace FontChanger.Presets;

/// <summary>Metrics of a font in ems.</summary>
public interface IFontProbe
{
    IEnumerable<Rune> Codepoints { get; }

    /// <summary>0 if unknown.</summary>
    float Ascent { get; }

    /// <summary>0 if unknown.</summary>
    float LineHeight { get; }

    bool TryGetGlyph(Rune rune, out GlyphBox box);

    bool HasBitmapOf(int ppem) => false;

    /// <summary>Whether DirectWrite recommends natural over natural symmetric rendering at a size, as the gasp table asks.</summary>
    bool PrefersNatural(float ppem) => false;
}
