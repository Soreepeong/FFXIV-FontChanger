using System;
using System.Collections.Generic;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.PluginInterface;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>
/// The game unloads its fonts and builds them again (a switch between the lobby's fonts and the game's, a UI shader reload),
/// possibly from other files, so everything made from them is made again.
/// </summary>
/// <remarks>
/// <ul>
/// <li>At once rather than at the next update, as a rebuilt glyph map may reuse addresses that cached glyphs are keyed by, and
/// copies borrow the map.</li>
/// <li>Both run in the game's update, outside any layout, so resetting there is as safe as between frames.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class FontLifecycleHooks : IDisposable
{
    private readonly GameGlyphReader _reader;
    private readonly Resetter _resetter;
    private readonly EnabledState _enabled;
    private readonly FrameLog _log;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();
    private readonly Hook<BuildFontDelegate> _buildFontHook;
    private readonly Hook<UnloadFontsDelegate> _unloadFontsHook;

    public FontLifecycleHooks(GameGlyphReader reader, Resetter resetter, EnabledState enabled, FrameLog log)
    {
        this._reader = reader;
        this._resetter = resetter;
        this._enabled = enabled;
        this._log = log;
        try
        {
            this._buildFontHook = this._finalizer.Add(
                Plugin.GameInterop.HookFromAddress<BuildFontDelegate>(AtkFontManager.Addresses.BuildFont.Value, this.BuildFontDetour));
            this._unloadFontsHook = this._finalizer.Add(
                Plugin.GameInterop.HookFromAddress<UnloadFontsDelegate>(AtkFontManager.Addresses.UnloadFonts.Value, this.UnloadFontsDetour));
            this._buildFontHook.Enable();
            this._unloadFontsHook.Enable();
        }
        catch
        {
            this._finalizer.Dispose();
            throw;
        }
    }

    private delegate int BuildFontDelegate(AtkFontManager* manager, ushort index);

    private delegate int UnloadFontsDelegate(AtkFontManager* manager);

    public void Dispose() => this._finalizer.Dispose();

    private int BuildFontDetour(AtkFontManager* manager, ushort index)
    {
        var result = this._buildFontHook.Original(manager, index);
        try
        {
            // UnloadFonts came first, and reads are done at Present.
            if (!this._reader.IsEmpty)
                Plugin.Log.Warning("A font was built with game glyph reads pending");
            this._resetter.Reset(ResetScope.SizedFonts);
            this._log.GameFontChanged();
        }
        catch (Exception ex)
        {
            this._enabled.Fail(ex, "Refreshing the copies of a rebuilt font failed");
        }

        return result;
    }

    private int UnloadFontsDetour(AtkFontManager* manager)
    {
        try
        {
            var textures = new HashSet<Pointer<KernelTexture>>();
            for (var i = 0; manager->Fonts is not null && i < manager->FontCount; i++)
            {
                var font = manager->Fonts + i;
                if (!font->Flags.HasFlag(AtkFontManager.FontFlags.Built))
                    continue;
                for (var t = 0; t < font->TextureCount && t < AtkFontManager.Font.MaxTextures; t++)
                {
                    if (font->Textures[t].Value is not null)
                        textures.Add(font->Textures[t]);
                }
            }

            this._reader.Forget(textures);
            this._resetter.Reset(ResetScope.SizedFonts);
            this._log.GameFontChanged();
        }
        catch (Exception ex)
        {
            this._reader.Clear();
            this._enabled.Fail(ex, "Letting go of the fonts being unloaded failed");
        }

        return this._unloadFontsHook.Original(manager);
    }
}
