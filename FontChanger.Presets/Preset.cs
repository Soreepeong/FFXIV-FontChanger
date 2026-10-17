using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

/// <summary>A FontChanger preset (XivRes.FontGenerator JSON): faces by game font name.</summary>
public sealed partial class Preset
{
    private Preset(Dictionary<string, Face> faces) => this.Faces = faces;

    public IReadOnlyDictionary<string, Face> Faces { get; }

    public IEnumerable<string> GlyphImageFolders =>
        this.Faces.Values.SelectMany(f => f.Elements).Select(e => e.GlyphImages?.Folder ?? string.Empty)
            .Where(f => f.Length != 0).Distinct();

    /// <summary>Makes a preset of faces; of faces of one name, the first is taken, as from a preset's file.</summary>
    public static Preset FromFaces(IEnumerable<Face> faces)
    {
        var res = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var face in faces)
            res.TryAdd(face.Name, face);
        return new(res);
    }

    public static Preset Combine(IEnumerable<Preset> presets)
    {
        var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, face) in preset.Faces)
                faces[name] = face;
        }

        return new(faces);
    }

    public static Preset Overlay(IEnumerable<Preset> presets)
    {
        var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, top) in preset.Faces)
            {
                if (!faces.TryGetValue(name, out var bottom) || bottom.Elements.Count == 0)
                {
                    faces[name] = top;
                    continue;
                }

                if (top.Elements.Count == 0)
                    continue;

                var bottomSize = bottom.Elements[0].Size;
                var topSize = top.Elements[0].Size;
                var factor = bottomSize > 0 && topSize > 0 ? bottomSize / topSize : 1;
                var elements = bottom.Elements.ToList();

                var leading = top.Elements.TakeWhile(e => e.Renderer != Renderer.Game).Count();
                for (var i = leading - 1; i >= 0; i--)
                {
                    if (top.Elements[i].MergeMode == MergeMode.AddNew)
                        elements.Add(top.Elements[i].Scaled(MergeMode.AddAll, factor));
                }

                for (var i = 0; i < top.Elements.Count; i++)
                {
                    if (i >= leading || top.Elements[i].MergeMode != MergeMode.AddNew)
                        elements.Add(top.Elements[i].Scaled(top.Elements[i].MergeMode, factor));
                }

                faces[name] = new() { Name = bottom.Name, Elements = elements, Alignment = bottom.Alignment };
            }
        }

        return new(faces);
    }

    public static string FamilyOf(string faceName)
    {
        if (GameFonts.TryFind(faceName, out var font))
            return font.Family;
        var underscore = faceName.LastIndexOf('_');
        return underscore <= 0 ? faceName : faceName[..underscore];
    }

    public Preset OnlyFamily(string family) =>
        new(
            this.Faces.Where(f => string.Equals(FamilyOf(f.Key), family, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase));

    public static uint Tag<T>(ReadOnlySpan<T> tag) where T : IBinaryInteger<T> => tag.Length switch
    {
        0 => 0x20202020u,
        1 => 0x20202000u | uint.CreateTruncating(tag[0]),
        2 => 0x20200000u | uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8),
        3 => 0x20000000u | uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8)
             | (uint.CreateTruncating(tag[2]) << 16),
        4 => uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8)
             | (uint.CreateTruncating(tag[2]) << 16) | (uint.CreateTruncating(tag[3]) << 24),
        _ => throw new ArgumentException("Invalid tag length", nameof(tag)),
    };

    public static uint Tag(string tag) => Tag(tag.AsSpan());
}
