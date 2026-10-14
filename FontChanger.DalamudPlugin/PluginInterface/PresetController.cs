using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Rendering;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed class PresetController : IDisposable
{
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration.Configuration configuration;
    private readonly FontReplacer replacer;
    private readonly Lock sync = new();
    private readonly Lock loadSync = new();
    private readonly Dictionary<string, FileSystemWatcher> presetWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> watchedPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemFonts systemFonts = new();
    private readonly Timer timer;
    private bool pendingSettingsLoad;
    private bool disposed;

    public PresetController(Configuration.Configuration configuration, FontReplacer replacer)
    {
        this.configuration = configuration;
        this.replacer = replacer;
        this.timer = new(_ => this.RunPending());
        this.Schedule(false, TimeSpan.Zero);
    }

    public string Status { get; private set; } = string.Empty;

    public bool SystemFallback => this.configuration.SystemFallback;

    public void Dispose()
    {
        lock (this.sync)
            this.disposed = true;
        this.timer.Dispose();

        // wait for the ongoing operation to complete
        this.loadSync.EnterScope().Dispose();

        this.WatchPresets([]);
        this.WatchFolders([]);
        this.systemFonts.Dispose();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this.configuration);

    public IReadOnlyList<FontSource> GetSources(string family)
    {
        lock (this.sync)
            return this.configuration.FamilySources.TryGetValue(family, out var sources) ? [.. sources] : [];
    }

    public void AddPresets(string family, IEnumerable<string> paths) => this.Change(c => GetChain(c, family).AddRange(
        paths.Select(p => new FontSource { Type = FontSourceType.Preset, Path = Path.GetFullPath(p.Trim().Trim('"')) })));

    public void AddFont(string family, FamilyFont font) =>
        this.Change(c => GetChain(c, family).Add(new() { Type = FontSourceType.SystemFont, Font = font }));

    public void Replace(string family, int index, FontSource source) => this.Change(c => {
        var chain = GetChain(c, family);
        if (index >= 0 && index < chain.Count)
            chain[index] = source;
    });

    public void SetEnabled(string family, int index, bool enabled) => this.Change(c => {
        var chain = GetChain(c, family);
        if (index >= 0 && index < chain.Count)
            chain[index] = chain[index] with { Enabled = enabled };
    });

    public void Remove(string family, int index) => this.Change(c => {
        var chain = GetChain(c, family);
        if (index >= 0 && index < chain.Count)
            chain.RemoveAt(index);
    });

    public int Move(string family, int index, int offset)
    {
        var target = index;
        this.Change(c => {
            var chain = GetChain(c, family);
            target = Math.Clamp(index + offset, 0, Math.Max(chain.Count - 1, 0));
            if (index < 0 || index >= chain.Count || target == index)
            {
                target = index;
                return;
            }

            var source = chain[index];
            chain.RemoveAt(index);
            chain.Insert(target, source);
        });
        return target;
    }

    public void Clear(string family) => this.Change(c => c.FamilySources.Remove(family));

    public void CopyToOthers(string family) => this.Change(c => {
        var copy = c.FamilySources.TryGetValue(family, out var chain) ? chain : [];
        foreach (var other in GameFontNames.Families)
        {
            if (string.Equals(other, family, StringComparison.OrdinalIgnoreCase))
                continue;
            if (copy.Count == 0)
                c.FamilySources.Remove(other);
            else
                c.FamilySources[other] = [.. copy];
        }
    });

    public void SetMonospacedDigits(string family, int index, bool monospaced) => this.Change(c => {
        var chain = GetChain(c, family);
        if (index >= 0 && index < chain.Count)
            chain[index] = chain[index] with { MonospacedDigits = monospaced };
    });

    public bool IsFamilyEnabled(string family)
    {
        lock (this.sync)
            return !this.configuration.DisabledFamilies.Contains(family);
    }

    public void SetFamilyEnabled(string family, bool enabled) =>
        this.Change(c => _ = enabled ? c.DisabledFamilies.Remove(family) : c.DisabledFamilies.Add(family));

    public void SetSystemFallback(bool systemFallback) => this.Change(c => c.SystemFallback = systemFallback);

    public void Reload() => this.Schedule(false, TimeSpan.Zero);

    private static List<FontSource> GetChain(Configuration.Configuration configuration, string family)
    {
        if (!configuration.FamilySources.TryGetValue(family, out var chain))
            configuration.FamilySources[family] = chain = [];
        return chain;
    }

    private void Change(Action<Configuration.Configuration> edit)
    {
        lock (this.sync)
            edit(this.configuration);
        this.Save();
        this.Schedule(false, TimeSpan.Zero);
    }

    private Preset? MakeFaces(string family, FamilyFont font, bool monospacedDigits, List<string> failures)
    {
        var opened = new Dictionary<(string, int, int, int), SystemFonts.Probe?>();

        bool TryOpen(LookupStruct l, [NotNullWhen(true)] out IFontProbe? probe)
        {
            var key = (l.Name, l.Weight, l.Stretch, l.Style);
            if (!opened.TryGetValue(key, out var cached))
            {
                this.systemFonts.TryOpen(l, out cached);
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

            var faces = GameFontNames.FacesOf(family)
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

    private void WatchPresets(IEnumerable<string> paths)
    {
        lock (this.sync)
        {
            var wanted = paths.ToList();
            this.watchedPresets.Clear();
            this.watchedPresets.UnionWith(wanted);

            var folders = wanted.Select(Path.GetDirectoryName).OfType<string>().Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in this.presetWatchers.Keys.Where(f => !folders.Contains(f)).ToList())
            {
                this.presetWatchers[folder].Dispose();
                this.presetWatchers.Remove(folder);
            }

            foreach (var folder in folders.Where(f => !this.presetWatchers.ContainsKey(f)))
            {
                var w = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                w.Changed += this.OnPresetChanged;
                w.Created += this.OnPresetChanged;
                w.Deleted += this.OnPresetChanged;
                w.Renamed += this.OnPresetChanged;
                w.EnableRaisingEvents = true;
                this.presetWatchers.Add(folder, w);
            }
        }
    }

    private void WatchFolders(IEnumerable<string> folders)
    {
        lock (this.sync)
        {
            var wanted = folders.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in this.folderWatchers.Keys.Where(f => !wanted.Contains(f)).ToList())
            {
                this.folderWatchers[folder].Dispose();
                this.folderWatchers.Remove(folder);
            }

            foreach (var folder in wanted.Where(f => !this.folderWatchers.ContainsKey(f)))
            {
                var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                w.Changed += this.OnGlyphImageChanged;
                w.Created += this.OnGlyphImageChanged;
                w.Deleted += this.OnGlyphImageChanged;
                w.Renamed += this.OnGlyphImageChanged;
                w.EnableRaisingEvents = true;
                this.folderWatchers.Add(folder, w);
            }
        }
    }

    private void OnPresetChanged(object sender, FileSystemEventArgs e)
    {
        var paths = e is RenamedEventArgs r ? new[] { e.FullPath, r.OldFullPath } : [e.FullPath];
        bool used;
        lock (this.sync)
            used = paths.Any(this.watchedPresets.Contains);
        if (used)
            this.Schedule(true, ReloadDelay);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(true, ReloadDelay);

    private void Schedule(bool edited, TimeSpan delay)
    {
        lock (this.sync)
        {
            this.pendingSettingsLoad |= !edited;
            if (!this.disposed)
                this.timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunPending()
    {
        lock (this.loadSync)
        {
            if (this.disposed)
                return;

            bool keepOnFailure;
            lock (this.sync)
            {
                keepOnFailure = !this.pendingSettingsLoad;
                this.pendingSettingsLoad = false;
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
        List<(string Family, List<FontSource> Sources)> families;
        lock (this.sync)
        {
            families = this.configuration.FamilySources
                           .Where(f => !this.configuration.DisabledFamilies.Contains(f.Key))
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
        var systemFallback = this.configuration.SystemFallback;
        Plugin.Framework.RunOnFrameworkThread(() => {
            if (!this.disposed)
                this.replacer.SetPreset(combined, systemFallback);
        }).ContinueWith(
            task => {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
