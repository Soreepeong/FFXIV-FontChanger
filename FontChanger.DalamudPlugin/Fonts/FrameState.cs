using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>
/// What the parts share: the frame that caches and atlas planes are aged by, whether fonts are replaced, the edge, and what
/// hooks and Present saw that other parts must react to at the next framework update.
/// </summary>
internal sealed class FrameState
{
    private readonly HashSet<Part> _failed = [];
    private bool _gameFontsChanged;
    private bool _planeReused;

    /// <summary>Gets the frame, advanced at the end of each Present.</summary>
    public long Frame { get; private set; }

    /// <summary>Gets or sets a value indicating whether fonts are replaced, read by every hook.</summary>
    public bool IsOn { get; set; } = true;

    /// <summary>Gets or sets the edge as drawn, which glyphs get margins for and copies claim their texture width by.</summary>
    public EdgeSettings Edge { get; set; } = new();

    public void EndFrame() => this.Frame++;

    /// <summary>The game built or unloaded fonts.</summary>
    public void GameFontChanged() => this._gameFontsChanged = true;

    /// <summary>A full atlas made room, so baked text may lack the glyphs that found none.</summary>
    public void PlaneReused() => this._planeReused = true;

    /// <summary>Stops replacing at once, and has the rest turned off at the next framework update.</summary>
    public void Fail(Exception ex, string message)
    {
        this.IsOn = false;
        CopyFonts.ForgetPicked();
        this.Fail(Part.Replacer, ex, message);
    }

    public void Fail(Part part, Exception ex, string message)
    {
        Plugin.Log.Error(ex, "{message}; turning it off", message);
        this._failed.Add(part);
    }

    /// <summary>Until the plugin is loaded again. A failed setup is known only to <see cref="FrameCoordinator.Failed"/>.</summary>
    public bool Failed(Part part) => this._failed.Contains(part);

    public Snapshot Drain()
    {
        var snapshot = new Snapshot(this._gameFontsChanged, this._planeReused);
        this._gameFontsChanged = false;
        this._planeReused = false;
        return snapshot;
    }

    public readonly record struct Snapshot(bool GameFontsChanged, bool PlaneReused);
}
