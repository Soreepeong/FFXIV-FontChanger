using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>What is being laid out, which is what the plugin keeps of each font set and the character LayOutCharacter is at.</summary>
/// <remarks>
/// <ul>
/// <li>A font set's marks are in its flags and pick scale in game memory, and in a table of the plugin's own.</li>
/// <li>The character is kept for GetGlyph, which isn't told which character of the text it looks up. One instance serves every
/// layout, as the game lays text out and draws only on the main thread.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class LayoutContext
{
    // Set by TextFlags.Bold (when drawing) and the bold macro. The game reads it nowhere.
    public const AtkFontManager.DrawFlags BoldFlag = AtkFontSetAdditions.UnusedDrawFlag26;

    // The game neither sets nor reads it, and restores the set's flags after each layout, so it can't outlive the text it marks.
    private const AtkFontManager.DrawFlags FaceFlag = AtkFontSetAdditions.UnusedDrawFlag25;

    private readonly (NodeFaces Faces, nint Text, int Length, int MainStart, int MainLength)[] _setFaces =
        new (NodeFaces, nint, int, int, int)[AtkFontManager.FontSetCount];

    // Text nodes with faces of their own, set by those who know them (nameplates, chat tabs).
    private readonly Dictionary<nint, NodeFaces> _nodeFaces = [];

    private Call _call;

    public Action<nint>? RefreshNodeFaces { get; set; }

    /// <summary>Gets a value indicating whether the text wrapper is laying text out.</summary>
    public bool Wrapping { get; private set; }

    /// <summary>Gets the character LayOutCharacter is at, null outside it.</summary>
    public byte* Character => this._call.Character;

    /// <summary>Gets a value indicating whether the character is laid out by an analyzer that only measures, so its glyph needs no pixels yet.</summary>
    public bool MeasuringOnly => this._call.MeasuringOnly;

    /// <summary>Gets a value indicating whether the character is in bold.</summary>
    public bool IsBold => this._call.State is not null && (this._call.State->Flags & BoldFlag) != 0;

    public static bool HasPickScale(AtkFontManager.FontSet* set)
    {
        var scale = set->PickScale;
        return scale > 0 && float.IsFinite(scale);
    }

    /// <returns>The call it replaces, for <see cref="LeaveCharacter"/>.</returns>
    public Call EnterCharacter(byte* character, byte* lineCharacter, bool measuringOnly, AtkFontAnalyzerState* state)
    {
        var outer = this._call;
        this._call = new() { Character = character, LineCharacter = lineCharacter, MeasuringOnly = measuringOnly, State = state };
        return outer;
    }

    public void LeaveCharacter(in Call outer)
    {
        // The italic macro reads the bit when italics end (the glyph after moves by the font's italic correction).
        if (this._call.ItalicCleared)
            this._call.State->Flags |= AtkFontManager.DrawFlags.Italic;
        this._call = outer;
    }

    /// <returns>The outer value, for <see cref="LeaveWrap"/>.</returns>
    public bool EnterWrap(bool wrapping)
    {
        var outer = this.Wrapping;
        this.Wrapping = wrapping;
        return outer;
    }

    public void LeaveWrap(bool outer) => this.Wrapping = outer;

    /// <summary>Whether the text wrapper is laying out text it has no scale of, which is wrapped to fit at every UI scale.</summary>
    /// <seealso cref="UiScaleWrapMeasurer"/>
    public bool IsWrapMeasure(AtkFontManager.FontSet* set) =>
        this.Wrapping && set->NodeScaleX == 1 && set->NodeScaleY == 1 && !HasPickScale(set);

    /// <summary>The size a copy is picked for.</summary>
    /// <remarks>
    /// The on-screen size, even where the game picked for another (a node's fixed font resolution, or a pick scale of its own),
    /// so that the renderer doesn't scale the glyphs. Text laid out unscaled takes its pick scale's, or the largest UI scale's
    /// while it is wrapped.
    /// </remarks>
    public float PickSizeOf(AtkFontManager.FontSet* set)
    {
        if (this.IsWrapMeasure(set))
            return set->ScaledSizeY * UiScaleWrapMeasurer.LargestScale;
        return set->NodeScaleX == 1 && set->NodeScaleY == 1 && HasPickScale(set)
                   ? set->UnscaledSizeY * set->PickScale
                   : set->ScaledSizeY;
    }

    /// <summary>The italics of the character.</summary>
    /// <remarks>
    /// An italic macro sets the state's bit, which measuring sees too. A text node's italics are only in the font set's own flags,
    /// which only drawing sees.
    /// </remarks>
    public ItalicMode ItalicModeOf(AtkFontManager.FontSet* set)
    {
        var state = this._call.State;
        if (state is null || (state->Flags & AtkFontManager.DrawFlags.Italic) == 0)
            return ItalicMode.Upright;
        return set is not null && (set->DrawFlags & AtkFontManager.DrawFlags.Italic) != 0
                   ? ItalicMode.Images
                   : ItalicMode.Real;
    }

    /// <summary>Clears the italic bit until the character is laid out, so that a real italic glyph is drawn unsheared.</summary>
    public void ClearItalic()
    {
        this._call.State->Flags &= ~AtkFontManager.DrawFlags.Italic;
        this._call.ItalicCleared = true;
    }

    /// <summary>Gives a text node faces of its own until <see cref="RemoveNodeFaces"/>.</summary>
    public void SetNodeFaces(nint node, NodeFaces faces) => this._nodeFaces[Misc.DebugAssertMainThread(node)] = faces;

    public void RemoveNodeFaces(nint node) => this._nodeFaces.Remove(Misc.DebugAssertMainThread(node));

    /// <summary>Default for a node no one set faces of.</summary>
    public NodeFaces GetNodeFaces(AtkTextNode* node) => this._nodeFaces.GetValueOrDefault((nint)node);

    public NodeFaces GetMeasuredNodeFaces(AtkTextNode* node)
    {
        this.RefreshNodeFaces?.Invoke((nint)node);
        return this.GetNodeFaces(node);
    }

    public bool TryGetNodeFaces(AtkTextNode* node, out NodeFaces faces) => this._nodeFaces.TryGetValue((nint)node, out faces);

    public void ApplyNodeFace(AtkFontManager.FontSet* set, AtkTextNode* node, NodeFaces faces)
    {
        if (faces.Face == TextFace.Default || IndexOf(set) is not (var index and >= 0))
        {
            set->DrawFlags &= ~FaceFlag;
            return;
        }

        var text = node is null ? null : (byte*)node->NodeText.StringPtr;
        var length = text is null ? 0 : GameUtf8.TextLength(text, node->NodeText.Length);
        var mainStart = 0;
        var mainLength = 0;
        if (faces.OtherLines == TextFace.Default || length == 0)
        {
            faces = faces with { OtherLines = TextFace.Default, Title = TitleLines.None };
        }
        else
        {
            switch (faces.Title)
            {
                case TitleLines.AllLines:
                    mainStart = -1;
                    break;
                case TitleLines.AfterFirstLine:
                {
                    var brk = GameUtf8.NextLineBreak(text, length, 0, out _);
                    if (brk < 0)
                        faces = faces with { OtherLines = TextFace.Default, Title = TitleLines.None };
                    else
                        mainLength = brk;
                    break;
                }
                case TitleLines.BeforeLastLine:
                {
                    var start = 0;
                    while (GameUtf8.NextLineBreak(text, length, start, out var brkLen) is var brk and >= 0)
                        start = brk + brkLen;
                    if (start == 0)
                    {
                        faces = faces with { OtherLines = TextFace.Default, Title = TitleLines.None };
                    }
                    else
                    {
                        mainStart = start;
                        mainLength = length - start;
                    }

                    break;
                }
                case TitleLines.None:
                default:
                    faces = faces with { OtherLines = TextFace.Default, Title = TitleLines.None };
                    break;
            }
        }

        this._setFaces[index] = (faces, (nint)text, length, mainStart, mainLength);
        set->DrawFlags |= FaceFlag;
    }

    public TextFace FaceOf(AtkFontManager.FontSet* set) =>
        (set->DrawFlags & FaceFlag) != 0 && IndexOf(set) is var index and >= 0 ? this._setFaces[index].Faces.Face : TextFace.Default;

    /// <summary>The face of the line the character is in, Default for the set's own.</summary>
    /// <remarks>Measuring lays a node's text out where it is. Drawing multiline text lays it out a line at a time, copied to the line buffer.</remarks>
    public TextFace LineFaceOf(AtkFontManager.FontSet* set)
    {
        var p = this._call.LineCharacter;
        if (p is null || (set->DrawFlags & FaceFlag) == 0 || IndexOf(set) is not (var index and >= 0))
            return TextFace.Default;
        var (faces, text, length, mainStart, mainLength) = this._setFaces[index];
        if (faces.OtherLines == TextFace.Default)
            return TextFace.Default;
        if (mainStart < 0)
            return faces.OtherLines;

        if ((nint)p >= text && (nint)p < text + length)
        {
            var at = (int)((nint)p - text);
            return at >= mainStart && at < mainStart + mainLength ? TextFace.Default : faces.OtherLines;
        }

        var buffer = AtkStage.Instance()->AtkFontManager->LineBuffer;
        if (buffer is null || p < buffer || p >= buffer + AtkFontManagerAdditions.LineBufferSize)
            return TextFace.Default;
        var bufLength = GameUtf8.TextLength(buffer, AtkFontManagerAdditions.LineBufferSize);
        if (p >= buffer + bufLength)
            return TextFace.Default;
        var line = LineAt(buffer, bufLength, (int)(p - buffer));
        return line.SequenceEqual(new((byte*)text + mainStart, mainLength)) ? TextFace.Default : faces.OtherLines;

        static ReadOnlySpan<byte> LineAt(byte* text, int length, int at)
        {
            var start = 0;
            while (GameUtf8.NextLineBreak(text, length, start, out var brkLen) is var brk and >= 0)
            {
                if (at < brk + brkLen)
                    return new(text + start, Math.Max(0, brk - start));
                start = brk + brkLen;
            }

            return new(text + start, length - start);
        }
    }

    private static int IndexOf(AtkFontManager.FontSet* set)
    {
        var manager = AtkStage.Instance()->AtkFontManager;
        var index = manager is null || manager->FontSets is null ? -1 : set - manager->FontSets;
        return index is >= 0 and < AtkFontManager.FontSetCount ? (int)index : -1;
    }

    /// <summary>The character LayOutCharacter is laying out.</summary>
    public struct Call
    {
        public byte* Character;

        public byte* LineCharacter;

        // Laid out by an analyzer that only measures, so its glyph needs no pixels yet.
        public bool MeasuringOnly;

        public AtkFontAnalyzerState* State;

        // The italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
        public bool ItalicCleared;
    }
}
