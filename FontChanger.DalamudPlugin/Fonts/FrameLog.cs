using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.PluginInterface;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>A part that turns itself off when it fails, until the plugin is loaded again.</summary>
internal enum Part
{
    Replacer,
    Subpixel,
}

/// <summary>What hooks and Present saw that other parts must react to, taken at the next framework update.</summary>
internal sealed class FrameLog
{
    private readonly HashSet<Part> _failures = [];
    private bool _gameFontsChanged;
    private bool _planeReused;

    /// <summary>The game built or unloaded fonts.</summary>
    public void GameFontChanged() => this._gameFontsChanged = true;

    /// <summary>A full atlas made room, so baked text may hold blanks it handed out.</summary>
    public void PlaneReused() => this._planeReused = true;

    public void Fail(Part part, Exception ex, string message)
    {
        Plugin.Log.Error(ex, "{message}; turning it off", message);
        this._failures.Add(part);
    }

    public Snapshot Drain()
    {
        var snapshot = new Snapshot(this._gameFontsChanged, this._planeReused, this._failures.Count == 0 ? [] : [.. this._failures]);
        this._gameFontsChanged = false;
        this._planeReused = false;
        this._failures.Clear();
        return snapshot;
    }

    public readonly record struct Snapshot(bool GameFontsChanged, bool PlaneReused, Part[] Failures);
}
