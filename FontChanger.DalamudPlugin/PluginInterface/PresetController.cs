using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed class PresetController : IDisposable
{
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration.Configuration _configuration;
    private readonly FontReplacer _replacer;
    private readonly Lock _loadSync = new();
    private readonly Dictionary<string, FileSystemWatcher> _presetWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;

    // Read by the watchers' threads, so it is replaced rather than changed.
    private ImmutableHashSet<string> _watchedPresets = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private bool _pendingSettingsLoad;
    private volatile bool _disposed;

    public PresetController(Configuration.Configuration configuration, FontReplacer replacer)
    {
        this._configuration = configuration;
        this._replacer = replacer;
        this._timer = new(_ => this.RunPending());
        this.Schedule(false);
    }

    public string Status { get; private set; } = string.Empty;

    public bool SystemFallback => this._configuration.SystemFallback;

    public void Dispose()
    {
        this._disposed = true;
        this._timer.Dispose();

        // A load in progress would otherwise add watchers back after they are removed.
        lock (this._loadSync)
        {
            this.WatchPresets([]);
            this.WatchGlyphImageFolders([]);
        }
    }

    public IReadOnlyList<FontSource> GetSources(string family) => GetChain(this._configuration, family);

    public void AddPresets(string family, IEnumerable<string> paths) => this.ChangeChain(family, chain => chain.AddRange(
        paths.Select(p => new FontSource { Type = FontSourceType.Preset, Path = Path.GetFullPath(p.Trim().Trim('"')) })));

    public void AddFont(string family, FamilyFont font) =>
        this.ChangeChain(family, chain => chain.Add(new() { Type = FontSourceType.SystemFont, Font = font }));

    public void Replace(string family, int index, FontSource source) =>
        this.ChangeChain(family, chain => index >= 0 && index < chain.Count ? chain.SetItem(index, source) : chain);

    public void Remove(string family, int index) =>
        this.ChangeChain(family, chain => index >= 0 && index < chain.Count ? chain.RemoveAt(index) : chain);

    public int Move(string family, int index, int offset)
    {
        var target = index;
        this.ChangeChain(family, chain => {
            target = Math.Clamp(index + offset, 0, Math.Max(chain.Count - 1, 0));
            if (index < 0 || index >= chain.Count || target == index)
            {
                target = index;
                return chain;
            }

            return chain.RemoveAt(index).Insert(target, chain[index]);
        });
        return target;
    }

    public void Clear(string family) => this.Change(c => c.FamilySources = c.FamilySources.Remove(family));

    public void CopyToOthers(string family) => this.Change(c => {
        var chain = GetChain(c, family);
        var sources = c.FamilySources.ToBuilder();
        foreach (var other in FontNames.Families)
        {
            if (string.Equals(other, family, StringComparison.OrdinalIgnoreCase))
                continue;
            if (chain.IsEmpty)
                sources.Remove(other);
            else
                sources[other] = chain;
        }

        c.FamilySources = sources.ToImmutable();
    });

    public bool IsFamilyEnabled(string family) => !this._configuration.DisabledFamilies.Contains(family);

    public void SetFamilyEnabled(string family, bool enabled) => this.Change(c =>
        c.DisabledFamilies = enabled ? c.DisabledFamilies.Remove(family) : c.DisabledFamilies.Add(family));

    public void SetSystemFallback(bool systemFallback) => this.Change(c => c.SystemFallback = systemFallback);

    public void Reload() => this.Schedule(false);

    private static ImmutableList<FontSource> GetChain(Configuration.Configuration configuration, string family) =>
        configuration.FamilySources.GetValueOrDefault(family, ImmutableList<FontSource>.Empty);

    private void ChangeChain(string family, Func<ImmutableList<FontSource>, ImmutableList<FontSource>> edit) => this.Change(c => {
        var chain = edit(GetChain(c, family));
        c.FamilySources = chain.IsEmpty ? c.FamilySources.Remove(family) : c.FamilySources.SetItem(family, chain);
    });

    private void Change(Action<Configuration.Configuration> edit)
    {
        edit(this._configuration);
        Plugin.PluginInterface.SavePluginConfig(this._configuration);

        this.Schedule(false);
    }

    // Edited files may be written in several steps, so they load a little later and a failure keeps the last loaded presets.
    private void Schedule(bool edited)
    {
        if (!edited)
            Volatile.Write(ref this._pendingSettingsLoad, true);
        if (this._disposed)
            return;
        try
        {
            this._timer.Change(edited ? ReloadDelay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Dispose can run between the check above and this call.
        }
    }

    private void RunPending()
    {
        lock (this._loadSync)
        {
            if (this._disposed)
                return;

            var keepOnFailure = !Interlocked.Exchange(ref this._pendingSettingsLoad, false);
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

        // Each is read once, as a change of the settings replaces them from another thread.
        var systemFallback = this._configuration.SystemFallback;
        var disabled = this._configuration.DisabledFamilies;
        var families = this._configuration.FamilySources
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
                    preset = this.MakeFonts(family, font, source.MonospacedDigits, source.Adjustments, above, failures);
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
                {
                    chain.Insert(0, preset);
                    above.Add(preset);
                }
            }

            perFamily[family] = chain;
        }

        if (keepOnFailure && failures.Count != 0)
        {
            this.Status = $"Changes not applied ({string.Join("; ", failures)}); keeping the last loaded";
            return;
        }

        var overlaid = Preset.Combine(perFamily.Values.Select(Preset.Overlay));
        var combined = overlaid.Fonts.Count == 0 ? null : overlaid;
        var familyCount = combined?.Fonts.Keys.Select(Preset.FamilyOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        var presetCount = loaded.Values.Count(p => p is not null);
        var status = combined is null
                         ? "The game's fonts"
                         : $"{combined.Fonts.Count} fonts of {familyCount} families"
                           + (presetCount != 0 ? $" from {presetCount} presets" : string.Empty)
                           + (fontCount != 0 ? $"; system fonts for {fontCount}" : string.Empty);
        if (failures.Count != 0)
            status += $"; loading failed ({string.Join("; ", failures)})";
        this.Status = status;

        this.WatchPresets(families.SelectMany(f => f.Sources).Where(s => s.Type == FontSourceType.Preset && s.Path.Length != 0).Select(s => s.Path));
        this.WatchGlyphImageFolders(combined?.GlyphImageFolders ?? []);
        Plugin.Framework.RunOnFrameworkThread(() => {
            if (!this._disposed)
                this._replacer.Fonts = new(combined, systemFallback);
        }).ContinueWith(
            task => {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // The fonts are made to go under those of the sources above, which are listed top first.
    private Preset? MakeFonts(
        string family,
        FamilyFont font,
        bool monospacedDigits,
        FontAdjustments adjustments,
        IReadOnlyList<Preset> above,
        List<string> failures)
    {
        this._loadSync.DebugAssertLockHeld();
        var opened = new Dictionary<(string, int, int, int), FontProbe?>();

        bool TryOpen(LookupStruct l, [NotNullWhen(true)] out IFontProbe? probe)
        {
            var key = (l.FamilyName, l.Weight, l.Stretch, l.Style);
            if (!opened.TryGetValue(key, out var cached))
            {
                this._replacer.Services.TryOpenProbe(l, out cached);
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

            var fonts = FontNames.FontNamesOf(family)
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

    private void WatchPresets(IEnumerable<string> paths)
    {
        this._loadSync.DebugAssertLockHeld();
        this._watchedPresets = paths.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        Watch(this._presetWatchers, this._watchedPresets.Select(Path.GetDirectoryName).OfType<string>(), this.OnPresetChanged);
    }

    private void WatchGlyphImageFolders(IEnumerable<string> folders)
    {
        this._loadSync.DebugAssertLockHeld();
        Watch(this._folderWatchers, folders, this.OnGlyphImageChanged);
    }

    private static void Watch(Dictionary<string, FileSystemWatcher> watchers, IEnumerable<string> folders, FileSystemEventHandler onChanged)
    {
        var wanted = folders.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in watchers.Keys.Where(f => !wanted.Contains(f)).ToList())
        {
            watchers[folder].Dispose();
            watchers.Remove(folder);
        }

        foreach (var folder in wanted.Where(f => !watchers.ContainsKey(f)))
        {
            var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            w.Changed += onChanged;
            w.Created += onChanged;
            w.Deleted += onChanged;
            w.Renamed += onChanged.Invoke;
            w.EnableRaisingEvents = true;
            watchers.Add(folder, w);
        }
    }

    private void OnPresetChanged(object sender, FileSystemEventArgs e)
    {
        var watched = this._watchedPresets;
        if (watched.Contains(e.FullPath) || (e is RenamedEventArgs r && watched.Contains(r.OldFullPath)))
            this.Schedule(true);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(true);
}
