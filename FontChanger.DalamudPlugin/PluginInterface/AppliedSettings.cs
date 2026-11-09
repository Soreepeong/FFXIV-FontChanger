using System.Collections.Generic;
using System.Linq;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Shaders;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>What the settings were when last applied, so that only changes are applied. Parts that failed stay off.</summary>
internal sealed class AppliedSettings
{
    // The configuration's names of the chat tabs' faces, by tab.
    private static readonly string[] ChatTabFaceNames =
        Enumerable.Range(0, TextFaceNames.ChatTabCount).Select(tab => TextFaceNames.NameOf(TextFaceNames.ChatTab(tab))).ToArray();

    private readonly HashSet<Part> _failed = [];

    // Null until first applied.
    private bool? _enabled;
    private EdgeSettings? _edge;
    private AtlasSettings? _atlas;
    private SubpixelShaders.SettingsRecord? _subpixel;
    private VerticalSubpixelRenderer? _verticalSubpixel;
    private NamePlateMode? _namePlates;
    private readonly int?[] _chatFontSizes = new int?[TextFaceNames.ChatTabCount];

    /// <summary>Gets what subpixel rendering was last applied with, such as the system's ClearType settings.</summary>
    public SubpixelShaders.SettingsRecord Subpixel => this._subpixel ?? default;

    /// <summary>Until the plugin is loaded again.</summary>
    public void Fail(Part part) => this._failed.Add(part);

    public bool Failed(Part part) => this._failed.Contains(part);

    /// <param name="configuration">The settings.</param>
    /// <param name="subpixel">The subpixel settings, read from the system if they follow it.</param>
    /// <returns>Null if nothing changed.</returns>
    public SettingsDelta? Diff(Configuration.Configuration configuration, SubpixelShaders.SettingsRecord subpixel)
    {
        var enabled = configuration.Enabled && !this.Failed(Part.Replacer);
        if (this.Failed(Part.Subpixel))
            subpixel = default;

        int?[]? chatFontSizes = null;
        for (var tab = 0; tab < ChatTabFaceNames.Length; tab++)
        {
            var size = configuration.FaceFontSizes.GetValueOrDefault(ChatTabFaceNames[tab]);
            if (this._chatFontSizes[tab] != size)
                (chatFontSizes ??= new int?[ChatTabFaceNames.Length])[tab] = size;
        }

        var delta = new SettingsDelta(
            Changed(this._enabled, enabled),
            Changed(this._edge, configuration.Edge),
            Changed(this._atlas, configuration.Atlas),
            Changed(this._subpixel, subpixel),
            Changed(this._verticalSubpixel, configuration.VerticalSubpixel),
            Changed(this._namePlates, configuration.NamePlates),
            chatFontSizes);
        return delta == SettingsDelta.None ? null : delta;

        static T? Changed<T>(T? applied, T wanted)
            where T : struct => applied is { } a && EqualityComparer<T>.Default.Equals(a, wanted) ? null : wanted;
    }

    public void Accept(SettingsDelta delta)
    {
        this._enabled = delta.Enabled ?? this._enabled;
        this._edge = delta.Edge ?? this._edge;
        this._atlas = delta.Atlas ?? this._atlas;
        this._subpixel = delta.Subpixel ?? this._subpixel;
        this._verticalSubpixel = delta.VerticalSubpixel ?? this._verticalSubpixel;
        this._namePlates = delta.NamePlates ?? this._namePlates;
        for (var tab = 0; tab < this._chatFontSizes.Length; tab++)
            this._chatFontSizes[tab] = delta.ChatFontSizes?[tab] ?? this._chatFontSizes[tab];
    }
}

/// <summary>The settings that changed since last applied, null for those that didn't.</summary>
/// <param name="ChatFontSizes">By chat tab, null if none changed.</param>
internal sealed record SettingsDelta(
    bool? Enabled,
    EdgeSettings? Edge,
    AtlasSettings? Atlas,
    SubpixelShaders.SettingsRecord? Subpixel,
    VerticalSubpixelRenderer? VerticalSubpixel,
    NamePlateMode? NamePlates,
    int?[]? ChatFontSizes)
{
    public static readonly SettingsDelta None = new(null, null, null, null, null, null, null);
}
