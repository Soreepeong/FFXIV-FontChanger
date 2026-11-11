using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>
/// Game fonts the preset has no font of (all, if it is null) keep the game's glyphs, and characters a font lacks come from
/// the game's glyphs unless <paramref name="SystemFallback"/>.
/// </summary>
internal readonly record struct FontSettings(
    Preset? Preset = null, bool SystemFallback = true, IReadOnlyDictionary<TextFace, Preset>? Faces = null);

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

/// <summary>What reading a request's sources came to.</summary>
/// <param name="Status">The status line.</param>
/// <param name="Watch">The files to watch from now on, null to keep watching the same.</param>
internal sealed record LoadReport(string Status, WatchList? Watch);

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

/// <summary>Reads the sources and makes the fonts on a task, which the framework thread takes once made. A request supersedes those before it.</summary>
internal sealed class FontLoader(FontServices services, SystemFallbackFonts fallbackFonts) : IDisposable
{
    private readonly Lock _sync = new();
    private FontSet? _ready;
    private string? _error;
    private LoadReport? _report;
    private long _serial;

    // A request that applies whatever fails hasn't begun reading its sources yet, so the one superseding it does too.
    private bool _mustApply;

    // The settings read last whose fonts weren't handed over, with what to watch for them. A later request that reads none
    // makes these, as its superseding them stopped their fonts from being made.
    private (FontSettings Settings, WatchList? Watch)? _unbuilt;
    private bool _stopped;
    private Task _chain = Task.CompletedTask;

    /// <summary>Takes what was made since the last call, then asks for the fonts of <paramref name="request"/>, if given.</summary>
    /// <remarks>In one step, so that a request can't supersede what the one before it finished making before that is taken.</remarks>
    /// <param name="request">What to make fonts from, null to only take.</param>
    /// <param name="keepOnFailure">Whether a source that can't be loaded leaves the fonts made last in place, as for an edited file.</param>
    /// <returns>The fonts, if any were made, why making them failed, and what reading the sources came to.</returns>
    public (FontSet? Fonts, string? Error, LoadReport? Report) Take(FontRequest? request, bool keepOnFailure)
    {
        Misc.DebugAssertMainThread();
        (FontSet? Fonts, string? Error, LoadReport? Report) taken;
        long serial;
        lock (this._sync)
        {
            taken = (this._ready, this._error, this._report);
            (this._ready, this._error, this._report) = (null, null, null);
            if (request is null)
                return taken;

            serial = ++this._serial;
            keepOnFailure &= !this._mustApply;
            this._mustApply = !keepOnFailure;
        }

        var value = request.Value;
        this._chain = this._chain.ContinueWith(_ => this.Load(value, keepOnFailure, serial), TaskScheduler.Default);
        return taken;
    }

    /// <summary>Waits for the loader, which stops at its next font.</summary>
    public void Dispose()
    {
        lock (this._sync)
            this._stopped = true;
        try
        {
            this._chain.Wait();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "The font loader's last task failed");
        }

        lock (this._sync)
        {
            this._ready?.Dispose();
            this._ready = null;
        }
    }

    /// <summary>A font that can't be made is left out with a warning.</summary>
    /// <returns>Null once <paramref name="stale"/>.</returns>
    private static FontSet? Build(FontServices services, SystemFallbackFonts fallbackFonts, FontSettings value, Func<bool> stale)
    {
        var set = FontSet.CreateGame(services, value.SystemFallback, fallbackFonts);
        var complete = false;
        try
        {
            set.Game.Prepare();
            if (!Materialize(set.Preset, value.Preset))
                return null;
            foreach (var (face, preset) in value.Faces ?? new Dictionary<TextFace, Preset>())
            {
                var fonts = new Dictionary<string, MaterializedFont>(StringComparer.OrdinalIgnoreCase);
                set.Faces[face] = fonts;
                if (!Materialize(fonts, preset))
                    return null;
                if (fonts.Count == 0)
                    set.Faces.Remove(face);
            }

            complete = !stale();
            return complete ? set : null;
        }
        finally
        {
            if (!complete)
                set.Dispose();
        }

        bool Materialize(Dictionary<string, MaterializedFont> fonts, Preset? preset)
        {
            foreach (var (name, def) in preset?.Fonts ?? new Dictionary<string, Font>())
            {
                if (stale())
                    return false;

                MaterializedFont? font = null;
                try
                {
                    font = new(services, def, value.SystemFallback, fallbackFonts, set.Sources);
                    font.Prepare();
                    fonts[name] = font;
                }
                catch (Exception ex)
                {
                    font?.Dispose();
                    Plugin.Log.Warning(ex, "The font {name} can't be set up; its text falls back", name);
                }
            }

            return true;
        }
    }

    private void Load(FontRequest value, bool keepOnFailure, long serial)
    {
        FontSet? made = null;
        string? error = null;
        bool stale;
        lock (this._sync)
        {
            stale = this._stopped || this._serial != serial;
            if (!stale)
                this._mustApply = false;
        }

        if (!stale)
        {
            FontSettings? settings = null;
            LoadReport report;
            try
            {
                (settings, report) = PresetCompiler.Compile(services, value, keepOnFailure);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the presets failed");
                report = new($"Loading failed ({ex.Message})", null);
            }

            // Before the fonts are made, which takes longer.
            lock (this._sync)
            {
                if (settings is { } own)
                    this._unbuilt = (own, report.Watch);
                else if (this._unbuilt is { } earlier)
                    (settings, report) = (earlier.Settings, report with { Watch = earlier.Watch });

                if (!this._stopped && this._serial == serial)
                    this._report = report;
            }

            if (settings is { } read)
            {
                try
                {
                    made = Build(services, fallbackFonts, read, () => this.IsStale(serial));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Setting up the fonts failed; the earlier ones stay");
                    error = ex.Message;
                }
            }
        }

        // Swapped, so that stale fonts are disposed after the lock.
        lock (this._sync)
        {
            var current = !this._stopped && this._serial == serial;

            // Settings whose fonts can't be made aren't tried again for a later request.
            if ((current && made is not null) || error is not null)
                this._unbuilt = null;
            if (current)
            {
                (this._ready, made) = (made, this._ready);
                this._error = error;
            }
        }

        made?.Dispose();
    }

    private bool IsStale(long serial) => Volatile.Read(ref this._stopped) || Interlocked.Read(ref this._serial) != serial;
}
