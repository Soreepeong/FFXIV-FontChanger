using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

// Loading the presets and system fonts the settings name, and applying them.
internal sealed partial class PresetController
{
    /// <summary>
    /// Loads the presets again: at once for a change of the settings; a little later for files <paramref name="edited"/>,
    /// which may be written in several steps (and which, if they fail to load, leave the last loaded as they are).
    /// </summary>
    private void Schedule(bool edited)
    {
        lock (this._sync)
        {
            this._pendingSettingsLoad |= !edited;
            if (!this._disposed)
                this._timer.Change(edited ? ReloadDelay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunPending()
    {
        lock (this._loadSync)
        {
            if (this._disposed)
                return;

            bool keepOnFailure;
            lock (this._sync)
            {
                keepOnFailure = !this._pendingSettingsLoad;
                this._pendingSettingsLoad = false;
            }

            try
            {
                this.Load(keepOnFailure);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the presets failed");
                this.Status = $"Loading failed ({ex.Message})";
            }
        }
    }

    private void Load(bool keepOnFailure)
    {
        this._loadSync.DebugAssertLockHeld();
        List<(string Family, List<FontSource> Sources)> families;
        bool systemFallback;
        lock (this._sync)
        {
            systemFallback = this._configuration.SystemFallback;
            families = this._configuration.FamilySources
                           .Where(f => !this._configuration.DisabledFamilies.Contains(f.Key))
                           .Select(f => (f.Key, f.Value.Where(s => s.Enabled).ToList()))
                           .Where(f => f.Item2.Count != 0)
                           .ToList();
        }

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
            foreach (var source in sources)
            {
                Preset? preset;
                if (source is { Type: FontSourceType.SystemFont, Font: { } font })
                {
                    preset = this.MakeFaces(family, font, source.MonospacedDigits, failures);
                    if (preset is not null)
                        fontCount++;
                }
                else if (source.Type == FontSourceType.Preset && source.Path.Length != 0)
                {
                    preset = Read(source.Path)?.OnlyFamily(family);
                }
                else
                {
                    continue;
                }

                if (preset is not null)
                    chain.Insert(0, preset);
            }

            perFamily[family] = chain;
        }

        if (keepOnFailure && failures.Count != 0)
        {
            this.Status = $"Changes not applied ({string.Join("; ", failures)}); keeping the last loaded";
            return;
        }

        var overlaid = Preset.Combine(perFamily.Values.Select(Preset.Overlay));
        var combined = overlaid.Faces.Count == 0 ? null : overlaid;
        var familyCount = combined?.Faces.Keys.Select(Preset.FamilyOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        var presetCount = loaded.Values.Count(p => p is not null);
        var status = combined is null
                         ? "The game's fonts"
                         : $"{combined.Faces.Count} faces of {familyCount} families"
                           + (presetCount != 0 ? $" from {presetCount} presets" : string.Empty)
                           + (fontCount != 0 ? $"; system fonts for {fontCount}" : string.Empty);
        if (failures.Count != 0)
            status += $"; loading failed ({string.Join("; ", failures)})";
        this.Status = status;

        this.WatchPresets(families.SelectMany(f => f.Sources).Where(s => s.Type == FontSourceType.Preset && s.Path.Length != 0).Select(s => s.Path));
        this.WatchFolders(combined?.GlyphImageFolders ?? []);
        Plugin.Framework.RunOnFrameworkThread(() => {
            if (!this._disposed)
                this._replacer.Faces = new(combined, systemFallback);
        }).ContinueWith(
            task => {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private Preset? MakeFaces(string family, FamilyFont font, bool monospacedDigits, List<string> failures)
    {
        this._loadSync.DebugAssertLockHeld();
        var opened = new Dictionary<(string, int, int, int), SystemFonts.Probe?>();

        bool TryOpen(LookupStruct l, [NotNullWhen(true)] out IFontProbe? probe)
        {
            var key = (l.Name, l.Weight, l.Stretch, l.Style);
            if (!opened.TryGetValue(key, out var cached))
            {
                this._systemFonts.TryOpen(l, out cached);
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
                failures.Add($"{family}: the font {font.Name} isn't installed");
                return null;
            }

            var faces = FontNames.FaceNamesOf(family)
                                 .Select(f => FaceFromFont.MakeFace(f.Name, f.Size, lookup, TryOpen, monospacedDigits))
                                 .OfType<Face>()
                                 .ToList();
            return faces.Count == 0 ? null : Preset.FromFaces(faces);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Making the faces of {family} with {font} failed", family, font.Name);
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
