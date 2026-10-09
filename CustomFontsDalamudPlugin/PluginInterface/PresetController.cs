using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CustomFonts;

/// <summary>Loads the configured preset into the replacer, and again whenever its file or its glyph images change.</summary>
internal sealed class PresetController : IDisposable
{
    // Editors write a file in several steps; a change is acted on once the file has been quiet this long.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration configuration;
    private readonly FontReplacer replacer;
    private readonly object sync = new();
    private readonly Dictionary<string, FileSystemWatcher> folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? pendingReload;

    public PresetController(Configuration configuration, FontReplacer replacer)
    {
        this.configuration = configuration;
        this.replacer = replacer;
        this.Watch();
        this.Load();
    }

    /// <summary>Gets what happened at the last load, for display.</summary>
    public string Status { get; private set; } = string.Empty;

    public void Dispose()
    {
        this.pendingReload?.Cancel();
        this.watcher?.Dispose();
        this.watcher = null;
        this.WatchFolders([]);
    }

    /// <summary>
    /// Uses a preset (or the built-in face, for an empty path), drawing characters it lacks with Windows' fallback fonts
    /// if <paramref name="systemFallback"/>; saves the choice, and watches the file.
    /// </summary>
    public void Apply(string path, bool systemFallback)
    {
        this.configuration.PresetPath = path.Trim().Trim('"');
        this.configuration.SystemFallback = systemFallback;
        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Watch();
        this.Load();
    }

    private void Watch()
    {
        this.watcher?.Dispose();
        this.watcher = null;
        var path = this.configuration.PresetPath;
        if (path.Length == 0 || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory || !Directory.Exists(directory))
            return;

        this.watcher = new(directory, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        this.watcher.Changed += this.OnFileChanged;
        this.watcher.Created += this.OnFileChanged;
        this.watcher.Renamed += this.OnFileChanged;
        this.watcher.EnableRaisingEvents = true;
    }

    /// <summary>Watches the folders of the preset's glyph images, and no others.</summary>
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
                w.Changed += this.OnFileChanged;
                w.Created += this.OnFileChanged;
                w.Deleted += this.OnFileChanged;
                w.Renamed += this.OnFileChanged;
                w.EnableRaisingEvents = true;
                this.folderWatchers.Add(folder, w);
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        this.pendingReload?.Cancel();
        this.pendingReload = new();
        Task.Delay(ReloadDelay, this.pendingReload.Token).ContinueWith(_ => this.Load(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Reads the preset (on the calling thread), and applies it on the framework thread.</summary>
    private void Load()
    {
        var path = this.configuration.PresetPath;
        Preset? preset = null;
        this.Status = "Built-in face";
        if (path.Length != 0)
        {
            try
            {
                preset = Preset.Load(path);
                this.Status = $"{preset.Faces.Count} faces from {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the preset {path} failed", path);
                this.Status = $"Loading failed ({ex.Message}); built-in face";
            }
        }

        this.WatchFolders(preset?.GlyphImageFolders ?? []);
        var systemFallback = this.configuration.SystemFallback;
        Plugin.Framework.RunOnFrameworkThread(() => this.replacer.SetPreset(preset, systemFallback)).ContinueWith(
            task =>
            {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
