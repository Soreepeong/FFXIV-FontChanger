using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>The files whose edits make the fonts again.</summary>
/// <param name="Presets">The preset files.</param>
/// <param name="PresetFolders">The folders they are in, of those that exist.</param>
/// <param name="GlyphImageFolders">The presets' glyph image folders, of those that exist.</param>
internal sealed record WatchList(ImmutableHashSet<string> Presets, ImmutableHashSet<string> PresetFolders, ImmutableHashSet<string> GlyphImageFolders)
{
    public static readonly WatchList None = Of([], []);

    /// <summary>Looks for the folders, so it is for the loader's task.</summary>
    public static WatchList Of(IEnumerable<string> presets, IEnumerable<string> glyphImageFolders)
    {
        var watched = presets.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            watched,
            watched.Select(Path.GetDirectoryName).OfType<string>().Where(Directory.Exists).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            glyphImageFolders.Where(Directory.Exists).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
