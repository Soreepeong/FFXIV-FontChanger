using System.Collections.Immutable;

using FontChanger.DalamudPlugin.Configuration;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>The settings the fonts are made from.</summary>
/// <remarks>The collections are replaced when edited, so two requests are equal only if none of it was.</remarks>
/// <param name="FamilySources">The sources, by family or face.</param>
/// <param name="DisabledFamilies">The families whose sources are left out.</param>
/// <param name="SystemFallback">Whether characters a font lacks come from the system's fonts.</param>
/// <param name="Reloads">How often the fonts were asked for again as they are.</param>
internal readonly record struct FontRequest(
    ImmutableDictionary<string, ImmutableList<FontSource>> FamilySources,
    ImmutableHashSet<string> DisabledFamilies,
    bool SystemFallback,
    int Reloads);
