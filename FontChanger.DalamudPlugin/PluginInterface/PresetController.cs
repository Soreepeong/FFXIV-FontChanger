using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>Edits the settings the fonts are made from, and asks the loader for fonts when they or a watched file change.</summary>
internal sealed class PresetController : IDisposable
{
    // Edited files may be written in several steps, so they load a little later and a failure keeps the last loaded presets.
    private const long ReloadDelayMs = 500;

    private readonly Configuration.Configuration _configuration;
    private readonly FontLoader _loader;
    private readonly Dictionary<string, FileSystemWatcher> _presetWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.OrdinalIgnoreCase);

    // Read by the watchers' threads, so it is replaced rather than changed.
    private ImmutableHashSet<string> _watchedPresets = WatchList.None.Presets;

    // When a watched file last changed, written by the watchers' threads. 0 if none did since the fonts were last asked for.
    private long _editedAt;

    private FontRequest? _requested;
    private int _reloads;

    public PresetController(Configuration.Configuration configuration, FontLoader loader)
    {
        this._configuration = configuration;
        this._loader = loader;
    }

    public string Status { get; private set; } = string.Empty;

    public bool SystemFallback => this._configuration.SystemFallback;

    public void Dispose() => this.Watch(WatchList.None);

    /// <summary>
    /// At the framework update. Takes what the loader made, and asks it for fonts if the settings they are made from changed or a
    /// watched file was edited.
    /// </summary>
    /// <returns>The fonts to apply, if any were made since the last call.</returns>
    public FontSet? Update()
    {
        Misc.DebugAssertMainThread();
        var c = this._configuration;
        var request = new FontRequest(c.FamilySources, c.DisabledFamilies, c.SystemFallback, this._reloads);
        var changed = request != this._requested;
        var editedAt = Volatile.Read(ref this._editedAt);
        var ask = changed || (editedAt != 0 && Environment.TickCount64 - editedAt >= ReloadDelayMs);
        if (ask)
        {
            // Unless a file was edited again meanwhile, which then asks again.
            Interlocked.CompareExchange(ref this._editedAt, 0, editedAt);
            this._requested = request;
        }

        var (fonts, error, report) = this._loader.Take(ask ? request : null, !changed);
        if (report is not null)
        {
            this.Status = report.Status;
            if (report.Watch is { } watch)
                this.Watch(watch);
        }

        // The fonts requested last couldn't be made, and the earlier ones stay.
        if (error is not null)
            this.Status = $"Applying failed ({error}); keeping the last loaded";
        return fonts;
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

    /// <summary>Makes the fonts again from the same settings, at the next framework update.</summary>
    public void Reload() => this._reloads++;

    private static ImmutableList<FontSource> GetChain(Configuration.Configuration configuration, string family) =>
        configuration.FamilySources.GetValueOrDefault(family, ImmutableList<FontSource>.Empty);

    private static void Watch(Dictionary<string, FileSystemWatcher> watchers, ImmutableHashSet<string> folders, FileSystemEventHandler onChanged)
    {
        foreach (var folder in watchers.Keys.Where(f => !folders.Contains(f)).ToList())
        {
            watchers[folder].Dispose();
            watchers.Remove(folder);
        }

        foreach (var folder in folders.Where(f => !watchers.ContainsKey(f)))
        {
            FileSystemWatcher? w = null;
            try
            {
                w = new(folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                w.Changed += onChanged;
                w.Created += onChanged;
                w.Deleted += onChanged;
                w.Renamed += onChanged.Invoke;
                w.EnableRaisingEvents = true;
                watchers.Add(folder, w);
            }
            catch (Exception ex)
            {
                // The folder may be gone since the loader looked for it.
                w?.Dispose();
                Plugin.Log.Warning(ex, "Watching {folder} failed", folder);
            }
        }
    }

    private void ChangeChain(string family, Func<ImmutableList<FontSource>, ImmutableList<FontSource>> edit) => this.Change(c => {
        var chain = edit(GetChain(c, family));
        c.FamilySources = chain.IsEmpty ? c.FamilySources.Remove(family) : c.FamilySources.SetItem(family, chain);
    });

    /// <summary>The fonts follow at the next framework update, which sees that the settings they are made from changed.</summary>
    private void Change(Action<Configuration.Configuration> edit)
    {
        edit(this._configuration);
        Plugin.PluginInterface.SavePluginConfig(this._configuration);
    }

    private void Watch(WatchList watch)
    {
        this._watchedPresets = watch.Presets;
        Watch(this._presetWatchers, watch.PresetFolders, this.OnPresetChanged);
        Watch(this._folderWatchers, watch.GlyphImageFolders, this.OnGlyphImageChanged);
    }

    private void OnPresetChanged(object sender, FileSystemEventArgs e)
    {
        var watched = this._watchedPresets;
        if (watched.Contains(e.FullPath) || (e is RenamedEventArgs r && watched.Contains(r.OldFullPath)))
            this.MarkEdited();
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.MarkEdited();

    private void MarkEdited() => Volatile.Write(ref this._editedAt, Environment.TickCount64);
}
