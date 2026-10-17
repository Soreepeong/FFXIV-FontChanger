using System;
using System.Collections.Generic;
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
    private readonly Lock _sync = new();
    private readonly Lock _loadSync = new();
    private readonly Dictionary<string, FileSystemWatcher> _presetWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchedPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemFonts _systemFonts = new();
    private readonly Timer _timer;
    private bool _pendingSettingsLoad;
    private bool _disposed;

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
        lock (this._sync)
            this._disposed = true;
        this._timer.Dispose();

        // wait for the ongoing operation to complete
        this._loadSync.EnterScope().Dispose();

        this.WatchPresets([]);
        this.WatchFolders([]);
        this._systemFonts.Dispose();
    }

    public IReadOnlyList<FontSource> GetSources(string family)
    {
        lock (this._sync)
            return this._configuration.FamilySources.TryGetValue(family, out var sources) ? [.. sources] : [];
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
        foreach (var other in FontNames.Families)
        {
            if (string.Equals(other, family, StringComparison.OrdinalIgnoreCase))
                continue;
            if (copy.Count == 0)
                c.FamilySources.Remove(other);
            else
                c.FamilySources[other] = [.. copy];
        }
    });

    public bool IsFamilyEnabled(string family)
    {
        lock (this._sync)
            return !this._configuration.DisabledFamilies.Contains(family);
    }

    public void SetFamilyEnabled(string family, bool enabled) =>
        this.Change(c => _ = enabled ? c.DisabledFamilies.Remove(family) : c.DisabledFamilies.Add(family));

    public void SetSystemFallback(bool systemFallback) => this.Change(c => c.SystemFallback = systemFallback);

    public void Reload() => this.Schedule(false);

    private static List<FontSource> GetChain(Configuration.Configuration configuration, string family)
    {
        if (!configuration.FamilySources.TryGetValue(family, out var chain))
            configuration.FamilySources[family] = chain = [];
        return chain;
    }

    private void Change(Action<Configuration.Configuration> edit)
    {
        lock (this._sync)
            edit(this._configuration);
        Plugin.PluginInterface.SavePluginConfig(this._configuration);
        this.Schedule(false);
    }
}
