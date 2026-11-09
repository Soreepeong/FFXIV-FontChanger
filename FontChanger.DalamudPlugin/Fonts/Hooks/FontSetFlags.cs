using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>What the plugin keeps of a font set, in its flags and pick scale in game memory, and in a table of its own.</summary>
internal sealed unsafe class FontSetFlags
{
    // Set by TextFlags.Bold (when drawing) and the bold macro. The game reads it nowhere.
    public const AtkFontManager.DrawFlags BoldFlag = (AtkFontManager.DrawFlags)(1u << 26);

    // The game neither sets nor reads it, and restores the set's flags after each layout, so it can't outlive the text it marks.
    private const AtkFontManager.DrawFlags FaceFlag = (AtkFontManager.DrawFlags)(1u << 25);

    // AtkFontManager's buffer that multiline text is drawn from, a line at a time.
    private const int LineBufferOffset = 0x48;
    private const int LineBufferSize = 0x2000;

    private readonly (NodeFaces Faces, nint Text, int Length)[] _setFaces = new (NodeFaces, nint, int)[AtkFontManager.FontSetCount];
    private readonly List<Func<Pointer<AtkTextNode>, NodeFaces?>> _nodeFaces = [];

    // Where positive, the game picks its font for this times the unscaled size, and scales the advances back.
    public static ref float PickScaleOf(AtkFontManager.FontSet* set) => ref *(float*)((byte*)set + 0x15C);

    public static float UnscaledSizeYOf(AtkFontManager.FontSet* set) => *(float*)((byte*)set + 0x124);

    public static bool IsBold(AtkFontAnalyzerState* state) => state is not null && (state->Flags & BoldFlag) != 0;

    /// <summary>The first to give a node's faces wins.</summary>
    public void AddNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._nodeFaces.Add(Misc.DebugAssertMainThread(faces));

    public void RemoveNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._nodeFaces.Remove(Misc.DebugAssertMainThread(faces));

    public NodeFaces GetNodeFaces(AtkTextNode* node)
    {
        foreach (var resolve in this._nodeFaces)
        {
            if (resolve(node) is { } faces)
                return faces;
        }

        return default;
    }

    public void ApplyNodeFace(AtkFontManager.FontSet* set, AtkTextNode* node, NodeFaces faces)
    {
        var index = IndexOf(set);
        if (faces.Face == TextFace.Default || index < 0)
        {
            set->DrawFlags &= ~FaceFlag;
            return;
        }

        // A name the game shows shortened is in no line, so no line is the title's.
        var text = node is null ? null : (byte*)node->NodeText.StringPtr;
        var length = text is null ? 0 : (int)node->NodeText.Length;
        if (faces.MainLine is not { } main || new ReadOnlySpan<byte>(text, length).IndexOf(main) < 0)
            faces = faces with { MainLine = null };
        this._setFaces[index] = (faces, (nint)text, length);
        set->DrawFlags |= FaceFlag;
    }

    public TextFace FaceOf(AtkFontManager.FontSet* set) =>
        (set->DrawFlags & FaceFlag) != 0 && IndexOf(set) is var index and >= 0 ? this._setFaces[index].Faces.Face : TextFace.Default;

    // Measuring lays a node's text out where it is. Drawing multiline text lays it out a line at a time, copied to the line buffer.
    public TextFace LineFaceOf(AtkFontManager.FontSet* set, byte* p)
    {
        if (p is null || (set->DrawFlags & FaceFlag) == 0 || IndexOf(set) is not (var index and >= 0))
            return TextFace.Default;
        var (faces, text, length) = this._setFaces[index];
        if (faces.MainLine is not { } main)
            return TextFace.Default;

        var buffer = *(byte**)((byte*)AtkStage.Instance()->AtkFontManager + LineBufferOffset);
        ReadOnlySpan<byte> line;
        if ((nint)p >= text && (nint)p < text + length)
            line = LineAround(new((byte*)text, length), (int)((nint)p - text));
        else if (buffer is not null && p >= buffer && p < buffer + LineBufferSize)
            line = LineAround(new(buffer, LineBufferSize), (int)(p - buffer));
        else
            return TextFace.Default;
        return line.IndexOf(main) < 0 ? faces.OtherLines : TextFace.Default;

        static ReadOnlySpan<byte> LineAround(ReadOnlySpan<byte> text, int at)
        {
            var start = text[..at].LastIndexOfAny((byte)'\n', (byte)'\r') + 1;
            var end = text[at..].IndexOfAny((byte)'\n', (byte)'\r', (byte)0);
            return text[start..(end < 0 ? text.Length : at + end)];
        }
    }

    private static int IndexOf(AtkFontManager.FontSet* set)
    {
        var manager = AtkStage.Instance()->AtkFontManager;
        var index = manager is null || manager->FontSets is null ? -1 : set - manager->FontSets;
        return index >= 0 && index < AtkFontManager.FontSetCount ? (int)index : -1;
    }
}
