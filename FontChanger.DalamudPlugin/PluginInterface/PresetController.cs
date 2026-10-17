using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Rendering;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class PresetController : IDisposable
{
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration.Configuration _configuration;
    private readonly FontReplacer _replacer;
    private readonly Lock _loadSync = new();
    private readonly Dictionary<string, FileSystemWatcher> _presetWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemFonts _systemFonts = new();
    private readonly Timer _timer;

    // The presets watched, read by the watchers' threads; replaced, not changed.
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

        // after the ongoing operation completes
        lock (this._loadSync)
        {
            this.WatchPresets([]);
            this.WatchFolders([]);
        }

        this._systemFonts.Dispose();
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

    /// <summary>Changes the sources of a family; one left with none is removed.</summary>
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
}
