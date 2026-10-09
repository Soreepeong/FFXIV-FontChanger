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
/// of a face in several), so a preset of every family can be used for one.
/// </summary>
internal sealed class PresetController : IDisposable
{
    // Editors write a file in several steps; a change is acted on once the file has been quiet this long.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration configuration;
    private readonly FontReplacer replacer;
    private readonly object sync = new();
    private readonly Dictionary<string, FileSystemWatcher> folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemFonts systemFonts = new();
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? pendingReload;
    private bool pendingScan;
    private bool pendingLoad;

    public PresetController(Configuration configuration, FontReplacer replacer)
    {
        this.configuration = configuration;
        this.replacer = replacer;
        configuration.FamilyPresets = new(configuration.FamilyPresets, StringComparer.OrdinalIgnoreCase);
        configuration.FamilyFonts = new(configuration.FamilyFonts ?? [], StringComparer.OrdinalIgnoreCase);

        // A preset file chosen before is the one selected preset of its folder.
        if (configuration.PresetPath is { } path)
        {
            path = path.Trim().Trim('"');
            if (path.Length != 0 && configuration.PresetFolder.Length == 0)
            {
                configuration.PresetFolder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
                configuration.SelectedPresets = [Path.GetFileName(path)];
            }

            configuration.PresetPath = null;
            Plugin.PluginInterface.SavePluginConfig(configuration);
        }

        // Presets selected for all families before are selected for each (once the families are known).
        if (configuration.SelectedPresets is { } selected && this.Families.Count != 0)
        {
            if (configuration.FamilyPresets.Count == 0)
            {
                foreach (var family in this.Families)
                    configuration.FamilyPresets[family] = [.. selected];
            }

            configuration.SelectedPresets = null;
            Plugin.PluginInterface.SavePluginConfig(configuration);
        }

        // JupiterN (Jupiter_45 and Jupiter_90) was part of Jupiter before configuration version 1: it takes Jupiter's
        // presets and font.
        if (configuration.Version < 1)
        {
            if (configuration.FamilyPresets.GetValueOrDefault("Jupiter") is { } jupiterPresets)
                configuration.FamilyPresets.TryAdd("JupiterN", [.. jupiterPresets]);
            if (configuration.FamilyFonts.GetValueOrDefault("Jupiter") is { } jupiterFont)
                configuration.FamilyFonts.TryAdd("JupiterN", jupiterFont);
            configuration.Version = 1;
            Plugin.PluginInterface.SavePluginConfig(configuration);
        }

        this.Watch();
        this.Scan();
        this.Load();
    }

    /// <summary>Gets what happened at the last load, for display.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Gets the presets in the preset folder, as of the last time it changed.</summary>
    public PresetTreeNode Tree { get; private set; } = PresetTreeNode.Empty;

    public string Folder => this.configuration.PresetFolder;

    public bool SystemFallback => this.configuration.SystemFallback;

    /// <summary>Gets the game's font families presets are selected for.</summary>
    public IReadOnlyList<string> Families => GameFontNames.Families;

    /// <summary>Gets the system's fonts, to choose from for a family.</summary>
    public SystemFonts Fonts => this.systemFonts;

    public void Dispose()
    {
        this.pendingReload?.Cancel();
        this.watcher?.Dispose();
        this.watcher = null;
        this.WatchFolders([]);
        this.systemFonts.Dispose();
    }

    /// <summary>Gets the system font a family's faces are drawn with, or null if they are of its presets.</summary>
    public FamilyFont? GetFont(string family)
    {
        lock (this.sync)
            return this.configuration.FamilyFonts.GetValueOrDefault(family);
    }

    /// <summary>
    /// Draws a family's faces, or those of all if null, with a system font over their presets; or with their presets again
    /// if <paramref name="font"/> is null.
    /// </summary>
    public void SetFont(string? family, FamilyFont? font)
    {
        lock (this.sync)
        {
            foreach (var f in family is null ? this.Families : (IReadOnlyList<string>)[family])
            {
                if (font is null)
                    this.configuration.FamilyFonts.Remove(f);
                else
                    this.configuration.FamilyFonts[f] = font;
            }
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>Gets whether system fonts' digits are made monospaced.</summary>
    public bool MonospacedDigits => this.configuration.MonospacedDigits;

    /// <summary>Sets whether system fonts' digits are made monospaced (tnum, or cells as wide as the 0).</summary>
    public void SetMonospacedDigits(bool monospaced)
    {
        this.configuration.MonospacedDigits = monospaced;
        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>Sets the system fonts of several families at once (null for their presets), loading once.</summary>
    public void SetFonts(IReadOnlyDictionary<string, FamilyFont?> fonts)
    {
        lock (this.sync)
        {
            foreach (var (f, font) in fonts)
            {
                if (font is null)
                    this.configuration.FamilyFonts.Remove(f);
                else
                    this.configuration.FamilyFonts[f] = font;
            }
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

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
        lock (this.sync)
        {
            if (!SamePath(folder, this.configuration.PresetFolder))
                this.configuration.FamilyPresets.Clear();
            this.configuration.PresetFolder = folder;
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Watch();
        this.Scan();
        this.Load();
    }

    /// <summary>
    /// Selects a preset for a family after those selected for it, so that its faces are used over theirs; or deselects it.
    /// </summary>
    public void Toggle(string family, string relativePath)
    {
        lock (this.sync)
        {
            if (!this.configuration.FamilyPresets.TryGetValue(family, out var selected))
                this.configuration.FamilyPresets[family] = selected = [];
            if (selected.RemoveAll(p => SameName(p, relativePath)) == 0)
                selected.Add(relativePath);
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>
    /// Deselects a preset for every family if all have it selected; else selects it, last, for those that don't.
    /// </summary>
    public void ToggleForAll(string relativePath)
    {
        lock (this.sync)
        {
            var families = this.Families;
            var all = families.Count != 0 && this.GetFamilyCount(relativePath) >= families.Count;
            foreach (var family in families)
            {
                if (!this.configuration.FamilyPresets.TryGetValue(family, out var selected))
                    this.configuration.FamilyPresets[family] = selected = [];
                if (all)
                    selected.RemoveAll(p => SameName(p, relativePath));
                else if (!selected.Exists(p => SameName(p, relativePath)))
                    selected.Add(relativePath);
            }
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>Deselects every preset and system font of a family, or of all if null, for the game's fonts (with fallbacks).</summary>
    public void ClearSelection(string? family)
    {
        lock (this.sync)
        {
            if (family is null)
            {
                this.configuration.FamilyPresets.Clear();
                this.configuration.FamilyFonts.Clear();
            }
            else
            {
                this.configuration.FamilyPresets.Remove(family);
                this.configuration.FamilyFonts.Remove(family);
            }
        }

        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>Sets whether characters the presets lack are drawn with Windows' fallback fonts, instead of the game's.</summary>
    public void SetSystemFallback(bool systemFallback)
    {
        this.configuration.SystemFallback = systemFallback;
        Plugin.PluginInterface.SavePluginConfig(this.configuration);
        this.Load();
    }

    /// <summary>Reads the folder and the selected presets again.</summary>
    public void Reload()
    {
        this.Scan();
        this.Load();
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes a family's faces with a system font (see <see cref="FaceFromFont"/>); null if it isn't installed.</summary>
    private Preset? MakeFaces(string family, FamilyFont font, List<string> failures)
    {
        var lookup = font.ToLookup();
        var others = new Dictionary<LookupDef, SystemFonts.Probe?>();
        try
        {
            using var probe = this.systemFonts.Open(lookup);
            if (probe is null)
            {
                failures.Add($"{family}: the font {font.Name} isn't installed");
                return null;
            }

            IFontProbe? OpenFont(LookupDef other)
            {
                if (!others.TryGetValue(other, out var opened))
                    others[other] = opened = this.systemFonts.Open(other);
                return opened;
            }

            var monospacedDigits = this.configuration.MonospacedDigits;
            var faces = GameFontNames.FacesOf(family)
                                     .Select(f => FaceFromFont.MakeFace(f.Name, f.Size, lookup, probe, OpenFont, monospacedDigits))
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
            foreach (var opened in others.Values)
                opened?.Dispose();
        }
    }

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
        this.Schedule(e.ChangeType != WatcherChangeTypes.Changed, load);
    }

    private void OnGlyphImageChanged(object sender, FileSystemEventArgs e) => this.Schedule(false, true);

    private void Schedule(bool scan, bool load)
    {
        if (!scan && !load)
            return;

        lock (this.sync)
        {
            this.pendingScan |= scan;
            this.pendingLoad |= load;
            this.pendingReload?.Cancel();
            this.pendingReload = new();
            Task.Delay(ReloadDelay, this.pendingReload.Token).ContinueWith(_ => this.RunPending(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    private void RunPending()
    {
        bool scan, load;
        lock (this.sync)
        {
            (scan, load) = (this.pendingScan, this.pendingLoad);
            this.pendingScan = this.pendingLoad = false;
        }

        if (scan)
            this.Scan();
        if (load)
            this.Load(true);
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
    /// Reads the selected presets (on the calling thread), and applies each family's faces from its presets on the
    /// framework thread. Presets that can't be read are left out, unless <paramref name="changed"/> (one was edited): then
    /// what was applied stays until they all read.
    /// </summary>
    private void Load(bool changed = false)
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
            var generated = this.MakeFaces(family, font, failures);
            if (generated is not null)
            {
                perFamily.Add(generated);
                fontFamilies++;
            }
        }

        if (changed && failures.Count != 0)
        {
            this.Status = $"Changes not applied ({string.Join("; ", failures)}); keeping the last loaded";
            return;
        }

        var preset = Preset.Combine(perFamily);
        if (preset.Faces.Count == 0)
            preset = null;
        var familyCount = perFamily.Count(p => p.Faces.Count != 0);
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
        Plugin.Framework.RunOnFrameworkThread(() => this.replacer.SetPreset(preset, systemFallback)).ContinueWith(
            task =>
            {
                Plugin.Log.Error(task.Exception!, "Applying the preset failed");
                this.Status = $"Applying failed ({task.Exception!.InnerException?.Message})";
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
