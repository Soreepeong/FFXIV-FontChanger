using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CustomFonts;

/// <summary>A folder of the preset folder, with the presets in it.</summary>
internal sealed class PresetTreeNode
{
    public static readonly PresetTreeNode Empty = new(string.Empty, [], []);

    public PresetTreeNode(string name, IReadOnlyList<PresetTreeNode> folders, IReadOnlyList<string> files)
    {
        this.Name = name;
        this.Folders = folders;
        this.Files = files;
    }

    public string Name { get; }

    public IReadOnlyList<PresetTreeNode> Folders { get; }

    /// <summary>Gets the presets' paths, relative to the preset folder.</summary>
    public IReadOnlyList<string> Files { get; }
}

/// <summary>
/// Loads the presets selected from the preset folder into the replacer, and again whenever their files or their glyph
/// images change. Presets are selected per game font family: a family's faces come from its presets only (the last one's
/// of a face in several), so a preset of every family can be used for one. Reading and loading happen on a background
/// thread, one at a time: right after the settings change, and once changed files have been quiet for a while.
/// </summary>
internal sealed class PresetController : IDisposable
{
    // Editors write a file in several steps; a change is acted on once the file has been quiet this long.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration configuration;
    private readonly FontReplacer replacer;
    private readonly object sync = new();
    private readonly object loadSync = new();
    private readonly Dictionary<string, FileSystemWatcher> folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemFonts systemFonts = new();
    private readonly Timer timer;
    private FileSystemWatcher? watcher;
    private bool pendingScan;
    private bool pendingLoad;
    private bool pendingSettingsLoad;
    private bool disposed;

    public PresetController(Configuration configuration, FontReplacer replacer)
    {
        this.configuration = configuration;
        this.replacer = replacer;
        this.timer = new(_ => this.RunPending());
        configuration.FamilyPresets = new(configuration.FamilyPresets, StringComparer.OrdinalIgnoreCase);
        configuration.FamilyFonts = new(configuration.FamilyFonts ?? [], StringComparer.OrdinalIgnoreCase);
        this.Watch();
        this.Schedule(true, true, false, TimeSpan.Zero);
    }

    /// <summary>Gets what happened at the last load, for display.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Gets the presets in the preset folder, as of the last time it changed.</summary>
    public PresetTreeNode Tree { get; private set; } = PresetTreeNode.Empty;

    public string Folder => this.configuration.PresetFolder;

    public bool SystemFallback => this.configuration.SystemFallback;

    /// <summary>Gets whether system fonts' digits are made monospaced.</summary>
    public bool MonospacedDigits => this.configuration.MonospacedDigits;

    /// <summary>Gets the game's font families presets are selected for.</summary>
    public IReadOnlyList<string> Families => GameFontNames.Families;

    public void Dispose()
    {
        lock (this.sync)
            this.disposed = true;
        this.timer.Dispose();

        // After a load that is running.
        lock (this.loadSync)
        {
        }

        this.watcher?.Dispose();
        this.watcher = null;
        this.WatchFolders([]);
        this.systemFonts.Dispose();
    }

    /// <summary>Saves the settings.</summary>
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this.configuration);

    /// <summary>Gets the system font a family's faces are drawn with, or null if they are of its presets.</summary>
    public FamilyFont? GetFont(string family)
    {
        lock (this.sync)
            return this.configuration.FamilyFonts.GetValueOrDefault(family);
    }

    /// <summary>
    /// Draws a family's faces, or those of all if null, with a system font over their presets; or with their presets again
    /// if <paramref name="font"/> is null. Unless <paramref name="save"/>, it is only tried (previewed), not saved.
    /// </summary>
    public void SetFont(string? family, FamilyFont? font, bool save = true) =>
        this.SetFonts(this.TargetFamilies(family).ToDictionary(f => f, _ => font), save);

    /// <summary>Sets the system fonts of several families at once (null for their presets).</summary>
    public void SetFonts(IReadOnlyDictionary<string, FamilyFont?> fonts, bool save = true) => this.Change(
        c =>
        {
            foreach (var (f, font) in fonts)
            {
                if (font is null)
                    c.FamilyFonts.Remove(f);
                else
                    c.FamilyFonts[f] = font;
            }
        },
        save: save);

    /// <summary>Sets whether system fonts' digits are made monospaced (tnum, or cells as wide as the 0).</summary>
    public void SetMonospacedDigits(bool monospaced) => this.Change(c => c.MonospacedDigits = monospaced);

    /// <summary>Gets the 1-based position of a preset in a family's selection order, or 0 if it isn't selected for it.</summary>
    public int GetSelectionOrder(string family, string relativePath)
    {
        lock (this.sync)
        {
            return this.configuration.FamilyPresets.TryGetValue(family, out var selected)
                       ? selected.FindIndex(p => SameName(p, relativePath)) + 1
                       : 0;
        }
    }

    /// <summary>Gets the number of families a preset is selected for.</summary>
    public int GetFamilyCount(string relativePath)
    {
        lock (this.sync)
            return this.configuration.FamilyPresets.Values.Count(selected => selected.Exists(p => SameName(p, relativePath)));
    }

    /// <summary>Gets the presets selected for a family, in order.</summary>
    public IReadOnlyList<string> GetSelection(string family)
    {
        lock (this.sync)
            return this.configuration.FamilyPresets.TryGetValue(family, out var selected) ? [.. selected] : [];
    }

    /// <summary>Chooses presets from another folder; the selection is cleared if the folder is a different one.</summary>
    public void SetFolder(string folder)
    {
        folder = folder.Trim().Trim('"');
        this.Change(
            c =>
            {
                if (!SamePath(folder, c.PresetFolder))
                    c.FamilyPresets.Clear();
                c.PresetFolder = folder;
            },
            scan: true);
        this.Watch();
    }

    /// <summary>
    /// Selects a preset for a family after those selected for it, so that its faces are used over theirs; or deselects it.
    /// </summary>
    public void Toggle(string family, string relativePath) => this.Change(c =>
    {
        if (!c.FamilyPresets.TryGetValue(family, out var selected))
            c.FamilyPresets[family] = selected = [];
        if (selected.RemoveAll(p => SameName(p, relativePath)) == 0)
            selected.Add(relativePath);
    });

    /// <summary>
    /// Deselects a preset for every family if all have it selected; else selects it, last, for those that don't.
    /// </summary>
    public void ToggleForAll(string relativePath) => this.Change(c =>
    {
        var families = this.Families;
        var all = families.Count != 0 && this.GetFamilyCount(relativePath) >= families.Count;
        foreach (var family in families)
        {
            if (!c.FamilyPresets.TryGetValue(family, out var selected))
                c.FamilyPresets[family] = selected = [];
            if (all)
                selected.RemoveAll(p => SameName(p, relativePath));
            else if (!selected.Exists(p => SameName(p, relativePath)))
                selected.Add(relativePath);
        }
    });

    /// <summary>Deselects every preset and system font of a family, or of all if null, for the game's fonts (with fallbacks).</summary>
    public void ClearSelection(string? family) => this.Change(c =>
    {
        if (family is null)
        {
            c.FamilyPresets.Clear();
            c.FamilyFonts.Clear();
        }
        else
        {
            c.FamilyPresets.Remove(family);
            c.FamilyFonts.Remove(family);
        }
    });

    /// <summary>Sets whether characters the presets lack are drawn with Windows' fallback fonts, instead of the game's.</summary>
    public void SetSystemFallback(bool systemFallback) => this.Change(c => c.SystemFallback = systemFallback);

    /// <summary>Reads the folder and the selected presets again.</summary>
    public void Reload() => this.Schedule(true, true, false, TimeSpan.Zero);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return a.Length == b.Length;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Gets a family, or every family if null.</summary>
    private IReadOnlyList<string> TargetFamilies(string? family) => family is null ? this.Families : [family];

    /// <summary>Changes the settings, saves them (unless not to), and loads the presets again at once.</summary>
    private void Change(Action<Configuration> edit, bool scan = false, bool save = true)
    {
        lock (this.sync)
            edit(this.configuration);
        if (save)
            this.Save();
        this.Schedule(scan, true, false, TimeSpan.Zero);
    }

    /// <summary>Makes a family's faces with a system font (see <see cref="FaceFromFont"/>); null if it isn't installed.</summary>
    private Preset? MakeFaces(string family, FamilyFont font, List<string> failures)
    {
        // Each font is opened once for all the faces.
        var opened = new Dictionary<(string, int, int, int), SystemFonts.Probe?>();
        IFontProbe? Open(LookupDef l)
        {
            var key = (l.Name, l.Weight, l.Stretch, l.Style);
            if (!opened.TryGetValue(key, out var probe))
                opened[key] = probe = this.systemFonts.Open(l);
            return probe;
        }

        try
        {
            var lookup = font.ToLookup();
            if (Open(lookup) is null)
            {
                failures.Add($"{family}: the font {font.Name} isn't installed");
                return null;
            }

            var monospacedDigits = this.configuration.MonospacedDigits;
            var faces = GameFontNames.FacesOf(family)
                                     .Select(f => FaceFromFont.MakeFace(f.Name, f.Size, lookup, Open, monospacedDigits))
                                     .OfType<FaceDef>()
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

    private void Watch()
    {
        this.watcher?.Dispose();
        this.watcher = null;
        var folder = this.configuration.PresetFolder;
        if (folder.Length == 0 || !Directory.Exists(folder))
            return;

        this.watcher = new(folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
        };
        this.watcher.Changed += this.OnPresetFolderChanged;
        this.watcher.Created += this.OnPresetFolderChanged;
        this.watcher.Deleted += this.OnPresetFolderChanged;
        this.watcher.Renamed += this.OnPresetFolderChanged;
        this.watcher.EnableRaisingEvents = true;
    }

    /// <summary>Watches the folders of the presets' glyph images, and no others.</summary>
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

    private void OnPresetFolderChanged(object sender, FileSystemEventArgs e)
    {
        var folder = this.configuration.PresetFolder;
        var paths = e is RenamedEventArgs r ? new[] { e.FullPath, r.OldFullPath } : [e.FullPath];
        var load = paths.Any(p => this.GetFamilyCount(Path.GetRelativePath(folder, p)) != 0);

        // Only files and folders coming and going change the tree, not the files' contents.
        this.Schedule(e.ChangeType != WatcherChangeTypes.Changed, load, true, ReloadDelay);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(false, true, true, ReloadDelay);

    /// <summary>
    /// Reads the folder and/or loads the presets after a delay, with whatever else is pending then. A load for files that
    /// changed (<paramref name="edited"/>) keeps what was applied if a preset can't be read, as it may be half written.
    /// </summary>
    private void Schedule(bool scan, bool load, bool edited, TimeSpan delay)
    {
        if (!scan && !load)
            return;

        lock (this.sync)
        {
            this.pendingScan |= scan;
            this.pendingLoad |= load;
            this.pendingSettingsLoad |= load && !edited;
            if (!this.disposed)
                this.timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunPending()
    {
        // One at a time, in order: each applies the settings as they are when it starts.
        lock (this.loadSync)
        {
            if (this.disposed)
                return;

            bool scan, load, keepOnFailure;
            lock (this.sync)
            {
                (scan, load, keepOnFailure) = (this.pendingScan, this.pendingLoad, !this.pendingSettingsLoad);
                this.pendingScan = this.pendingLoad = this.pendingSettingsLoad = false;
            }

            try
            {
                if (scan)
                    this.Scan();
                if (load)
                    this.Load(keepOnFailure);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the presets failed");
                this.Status = $"Loading failed ({ex.Message})";
            }
        }
    }

    /// <summary>Reads the preset folder's tree of presets (JSON files); folders without any are left out.</summary>
    private void Scan()
    {
        var root = this.configuration.PresetFolder;
        try
        {
            this.Tree = root.Length != 0 && Directory.Exists(root) ? ScanFolder(root, string.Empty) ?? PresetTreeNode.Empty : PresetTreeNode.Empty;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Reading the preset folder {folder} failed", root);
            this.Tree = PresetTreeNode.Empty;
        }

        static PresetTreeNode? ScanFolder(string root, string relative)
        {
            var path = Path.Combine(root, relative);
            var options = new EnumerationOptions { IgnoreInaccessible = true };
            var folders = Directory.EnumerateDirectories(path, "*", options)
                                   .Order(StringComparer.OrdinalIgnoreCase)
                                   .Select(d => ScanFolder(root, Path.Combine(relative, Path.GetFileName(d))))
                                   .OfType<PresetTreeNode>()
                                   .ToList();
            var files = Directory.EnumerateFiles(path, "*.json", options)
                                 .Order(StringComparer.OrdinalIgnoreCase)
                                 .Select(f => Path.Combine(relative, Path.GetFileName(f)))
                                 .ToList();
            return folders.Count == 0 && files.Count == 0 ? null : new(Path.GetFileName(path), folders, files);
        }
    }

    /// <summary>
    /// Reads the selected presets, and applies each family's faces from its presets on the framework thread. Presets that
    /// can't be read are left out, unless <paramref name="keepOnFailure"/>: then what was applied stays until they all read.
    /// </summary>
    private void Load(bool keepOnFailure)
    {
        string folder;
        List<(string Family, List<string> Selected)> families;
        List<(string Family, FamilyFont Font)> fonts;
        lock (this.sync)
        {
            folder = this.configuration.PresetFolder;
            families = this.configuration.FamilyPresets.Where(f => f.Value.Count != 0).Select(f => (f.Key, f.Value.ToList())).ToList();
            fonts = this.configuration.FamilyFonts.Select(f => (f.Key, f.Value)).ToList();
        }

        // Each preset is read once, however many families use it.
        var loaded = new Dictionary<string, Preset?>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        Preset? Read(string relative)
        {
            if (loaded.TryGetValue(relative, out var known))
                return known;
            var path = Path.Combine(folder, relative);
            try
            {
                return loaded[relative] = Preset.Load(path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Loading the preset {path} failed", path);
                failures.Add($"{relative}: {ex.Message}");
                return loaded[relative] = null;
            }
        }

        var perFamily = families.Select(f => Preset.Combine(f.Selected.Select(Read).OfType<Preset>()).OnlyFamily(f.Family)).ToList();

        // The families' system fonts, over their presets.
        var fontFamilies = 0;
        foreach (var (family, font) in fonts)
        {
            if (this.MakeFaces(family, font, failures) is { } generated)
            {
                perFamily.Add(generated);
                fontFamilies++;
            }
        }

        if (keepOnFailure && failures.Count != 0)
        {
            this.Status = $"Changes not applied ({string.Join("; ", failures)}); keeping the last loaded";
            return;
        }

        var preset = Preset.Combine(perFamily);
        if (preset.Faces.Count == 0)
            preset = null;
        var familyCount = preset?.Faces.Keys.Select(Preset.FamilyOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        var presetCount = loaded.Values.Count(p => p is not null);
        var status = preset is null
                         ? "The game's fonts"
                         : $"{preset.Faces.Count} faces of {familyCount} families"
                           + (presetCount != 0 ? $" from {presetCount} presets" : string.Empty)
                           + (fontFamilies != 0 ? $"; system fonts for {fontFamilies}" : string.Empty);
        if (failures.Count != 0)
            status += $"; loading failed ({string.Join("; ", failures)})";
        this.Status = status;

        this.WatchFolders(preset?.GlyphImageFolders ?? []);
        var systemFallback = this.configuration.SystemFallback;
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            // Not once the replacer is gone, which unloading the plugin does right after disposing this.
            if (!this.disposed)
                this.replacer.SetPreset(preset, systemFallback);
        }).ContinueWith(
            task =>
            {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
