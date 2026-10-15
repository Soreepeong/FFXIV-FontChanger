using System;

using FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>An element of a face: its definition, and the font it draws with (<see cref="IElementFont"/>).</summary>
internal sealed class ReplacementElement(FaceElement def, IElementFont font) : IDisposable
{
    public FaceElement Def { get; } = def;

    public IElementFont Font { get; } = font;

    /// <summary>Gets the element's font if its glyphs are shaped with DirectWrite: those of a font, not merged or images.</summary>
    public OutlineElementFont? Shaped => this.Font as OutlineElementFont;

    public bool DrawsGame => this.Font is GameElementFont;

    public void Dispose() => this.Font.Dispose();
}