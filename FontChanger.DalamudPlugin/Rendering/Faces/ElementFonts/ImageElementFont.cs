using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

internal sealed unsafe class ImageElementFont(GlyphImages? images, ImageRenderer? renderer, Matrix2x2 transform, byte[]? gamma) : IElementFont
{
    public bool CanDraw => images is not null && renderer is not null;

    public bool Has(Rune rune, AtkFontManager.Font* game) => images?.Has(rune) ?? false;

    public IEnumerable<Rune> Codepoints => images?.Codepoints ?? [];

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game) => this.GetLineMetrics(px, transform);

    public (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 t) => images?.GetLineMetrics(px, t) ?? (0, 0);

    public bool TryGetBaseline(uint tag, float px, out float baseline)
    {
        baseline = 0;
        return false;
    }

    public RasterGlyph? Rasterize(Rune rune, float px) => this.Rasterize(rune, px, transform);

    public RasterGlyph? Rasterize(Rune rune, float px, Matrix2x2 t) =>
        images is not null && renderer is not null && images.Rasterize(renderer, rune, px, t) is { } g
            ? gamma is null ? g : g.WithCoverage(gamma)
            : null;

    public float GetAdvance(Rune rune, float px) => this.Rasterize(rune, px)?.Advance ?? 0;

    public int GetKerning(Rune left, Rune right, float px, Matrix2x2 t) => images?.GetKerning(left, right, px, t) ?? 0;

    public void Dispose()
    {
    }
}
