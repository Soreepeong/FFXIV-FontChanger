using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class PresetController
{
    /// <summary>Watches the folders of the presets used, for changes to them.</summary>
    private void WatchPresets(IEnumerable<string> paths)
    {
        this._loadSync.DebugAssertLockHeld();
        this._watchedPresets = paths.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        WatchFolders(this._presetWatchers, this._watchedPresets.Select(Path.GetDirectoryName).OfType<string>(), this.OnPresetChanged);
    }

    /// <summary>Watches the folders glyph images are read from.</summary>
    private void WatchFolders(IEnumerable<string> folders)
    {
        this._loadSync.DebugAssertLockHeld();
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
        if (paths.Any(this._watchedPresets.Contains))
            this.Schedule(true);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(true);
}
