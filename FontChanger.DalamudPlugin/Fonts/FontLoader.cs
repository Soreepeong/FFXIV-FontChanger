using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

/// <summary>Makes fonts on a task, which the framework thread takes once made. A request supersedes those before it.</summary>
internal sealed class FontLoader(FontServices services, SystemFallbackFonts fallbackFonts) : IDisposable
{
    private readonly Lock _sync = new();
    private FontSet? _ready;
    private string? _error;
    private long _serial;
    private bool _stopped;
    private Task _chain = Task.CompletedTask;

    public void Request(FontSettings value)
    {
        Misc.DebugAssertMainThread();
        FontSet? superseded;
        long serial;
        lock (this._sync)
        {
            serial = ++this._serial;
            superseded = this._ready;
            this._ready = null;
            this._error = null;
        }

        superseded?.Dispose();
        this._chain = this._chain.ContinueWith(_ => this.Load(value, serial), TaskScheduler.Default);
    }

    /// <summary>The fonts made since the last call, if any, or why the last request failed.</summary>
    public (FontSet? Fonts, string? Error) TryTake()
    {
        lock (this._sync)
        {
            var taken = (this._ready, this._error);
            this._ready = null;
            this._error = null;
            return taken;
        }
    }

    /// <summary>Waits for the loader, which stops at its next font.</summary>
    public void Dispose()
    {
        lock (this._sync)
            this._stopped = true;
        this._chain.Wait();
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

    private void Load(FontSettings value, long serial)
    {
        FontSet? made = null;
        string? error = null;
        try
        {
            made = Build(services, fallbackFonts, value, () => Volatile.Read(ref this._stopped) || Interlocked.Read(ref this._serial) != serial);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Setting up the fonts failed; the earlier ones stay");
            error = ex.Message;
        }

        // Swapped, so that stale fonts are disposed after the lock.
        lock (this._sync)
        {
            if (!this._stopped && this._serial == serial)
            {
                (this._ready, made) = (made, this._ready);
                this._error = error;
            }
        }

        made?.Dispose();
    }
}
