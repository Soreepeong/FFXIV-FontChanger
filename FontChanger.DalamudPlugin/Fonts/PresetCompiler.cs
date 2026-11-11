using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Reads a request's presets and makes its system fonts' presets, on the loader's task.</summary>
internal static class PresetCompiler
{
    /// <summary>Makes the settings of the fonts <paramref name="request"/> asks for.</summary>
    /// <param name="services">Opens the system fonts.</param>
    /// <param name="request">What the fonts are made from.</param>
    /// <param name="keepOnFailure">Whether a source that can't be loaded leaves the fonts loaded last in place.</param>
    /// <returns>The fonts' settings, null if the fonts loaded last are kept, and what to show and watch.</returns>
    public static (FontSettings? Settings, LoadReport Report) Compile(FontServices services, FontRequest request, bool keepOnFailure)
    {
        var disabled = request.DisabledFamilies;
        var families = request.FamilySources
                              .Where(f => !disabled.Contains(f.Key))
                              .Select(f => (Family: f.Key, Sources: f.Value.Where(s => s.Enabled).ToList()))
                              .Where(f => f.Sources.Count != 0)
                              .ToList();

        var loaded = new Dictionary<string, Preset?>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        Preset? Read(string path)
        {
            if (loaded.TryGetValue(path, out var known))
                return known;
            try
            {
                return loaded[path] = Preset.Load(path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the preset {path} failed", path);
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                return loaded[path] = null;
            }
        }

        var perFamily = new Dictionary<string, List<Preset>>(StringComparer.OrdinalIgnoreCase);
        var fontCount = 0;
        foreach (var (family, sources) in families)
        {
            var chain = new List<Preset>();
            var above = new List<Preset>();
            foreach (var source in sources)
            {
                Preset? preset;
                if (source is { Type: FontSourceType.SystemFont, Font: { } font })
                {
                    preset = MakeFonts(services, family, font, source.MonospacedDigits, source.Adjustments, above, failures);
                    if (preset is not null)
                        fontCount++;
                }
                else if (source.Type == FontSourceType.Preset && source.Path.Length != 0)
                {
                    preset = Read(source.Path);
                    if (!IsFace(family))
                        preset = preset?.OnlyFamily(family);
                }
                else
                {
                    continue;
                }

                if (preset is not null)
                {
                    chain.Insert(0, preset);
                    above.Add(preset);
                }
            }

            perFamily[family] = chain;
        }

        if (keepOnFailure && failures.Count != 0)
            return (null, new($"Changes not applied ({string.Join("; ", failures)}); keeping the last loaded", null));

        var faces = new Dictionary<TextFace, Preset>();
        foreach (var (family, chain) in perFamily)
        {
            if (TextFaceNames.TryGetFace(family, out var face) && Preset.Overlay(chain) is { Fonts.Count: > 0 } facePreset)
                faces[face] = facePreset;
        }

        var overlaid = Preset.Combine(perFamily.Where(f => !IsFace(f.Key)).Select(f => Preset.Overlay(f.Value)));
        var combined = overlaid.Fonts.Count == 0 ? null : overlaid;
        var familyCount = combined?.Fonts.Keys.Select(Preset.FamilyOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        var presetCount = loaded.Values.Count(p => p is not null);
        var status = combined is null
                         ? "The game's fonts"
                         : $"{combined.Fonts.Count} fonts of {familyCount} families"
                           + (presetCount != 0 ? $" from {presetCount} presets" : string.Empty)
                           + (fontCount != 0 ? $"; system fonts for {fontCount}" : string.Empty);
        foreach (var (face, facePreset) in faces)
            status += $"; {facePreset.Fonts.Count} for {TextFaceNames.NameOf(face).ToLowerInvariant()}";
        if (failures.Count != 0)
            status += $"; loading failed ({string.Join("; ", failures)})";

        var watch = WatchList.Of(
            families.SelectMany(f => f.Sources).Where(s => s.Type == FontSourceType.Preset && s.Path.Length != 0).Select(s => s.Path),
            (combined?.GlyphImageFolders ?? []).Concat(faces.Values.SelectMany(p => p.GlyphImageFolders)).Distinct(StringComparer.OrdinalIgnoreCase));
        return (new FontSettings(combined, request.SystemFallback, faces), new(status, watch));
    }

    private static bool IsFace(string family) => TextFaceNames.TryGetFace(family, out _);

    // A face's text may be in any family's fonts.
    private static IEnumerable<(string Name, float Size)> FontNamesOf(string family) =>
        IsFace(family) ? FontNames.Families.SelectMany(FontNames.FontNamesOf) : FontNames.FontNamesOf(family);

    // The fonts are made to go under those of the sources above, which are listed top first.
    private static Preset? MakeFonts(
        FontServices services,
        string family,
        FamilyFont font,
        bool monospacedDigits,
        FontAdjustments adjustments,
        IReadOnlyList<Preset> above,
        List<string> failures)
    {
        var opened = new Dictionary<(string, int, int, int), FontProbe?>();

        bool TryOpen(LookupStruct l, [NotNullWhen(true)] out IFontProbe? probe)
        {
            var key = (l.FamilyName, l.Weight, l.Stretch, l.Style);
            if (!opened.TryGetValue(key, out var cached))
            {
                services.TryOpenProbe(l, out cached);
                opened[key] = cached;
            }

            probe = cached;
            return probe is not null;
        }

        try
        {
            var lookup = font.ToLookup();
            if (!TryOpen(lookup, out _))
            {
                failures.Add($"{family}: the font {font.FamilyName} isn't installed");
                return null;
            }

            var fonts = FontNamesOf(family)
                                 .Select(
                                     f => FontsFromSystemFont.MakeFont(
                                         f.Name,
                                         f.Size,
                                         lookup,
                                         TryOpen,
                                         monospacedDigits,
                                         GameFontProbe.Open(f.Name),
                                         above.Select(p => p.Fonts.TryGetValue(f.Name, out var drawn) ? FontsFromSystemFont.ProbeFont(drawn, TryOpen) : null)
                                              .OfType<IFontProbe>()
                                              .ToList(),
                                         adjustments))
                                 .OfType<Font>()
                                 .ToList();
            return fonts.Count == 0 ? null : Preset.FromFonts(fonts);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Making the fonts of {family} with {font} failed", family, font.FamilyName);
            failures.Add($"{family}: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var probe in opened.Values)
                probe?.Dispose();
        }
    }
}
