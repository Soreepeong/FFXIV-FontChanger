using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FontChanger.DalamudPlugin.PluginInterface;

// Watching the presets and glyph image folders used, to load them again when they change.
internal sealed partial class PresetController
{
    /// <summary>Watches the folders of the presets used, for changes to them.</summary>
    private void WatchPresets(IEnumerable<string> paths)
    {
        lock (this._sync)
        {
            var wanted = paths.ToList();
            this._watchedPresets.Clear();
            this._watchedPresets.UnionWith(wanted);
            WatchFolders(this._presetWatchers, wanted.Select(Path.GetDirectoryName).OfType<string>(), this.OnPresetChanged);
        }
    }

    /// <summary>Watches the folders glyph images are read from.</summary>
    private void WatchFolders(IEnumerable<string> folders)
    {
        lock (this._sync)
            WatchFolders(this._folderWatchers, folders, this.OnGlyphImageChanged);
    }

    /// <summary>Makes <paramref name="watchers"/> watch the folders that are there of <paramref name="folders"/>, and only those.</summary>
    private static void WatchFolders(Dictionary<string, FileSystemWatcher> watchers, IEnumerable<string> folders, FileSystemEventHandler onChanged)
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
        var paths = e is RenamedEventArgs r ? new[] { e.FullPath, r.OldFullPath } : [e.FullPath];
        bool used;
        lock (this._sync)
            used = paths.Any(this._watchedPresets.Contains);
        if (used)
            this.Schedule(true);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(true);
}
