using System;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;

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
    private readonly Action<ResetScope> _reset;
    private readonly FrameState _state;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();
    private readonly Hook<BuildFontDelegate> _buildFontHook;
    private readonly Hook<UnloadFontsDelegate> _unloadFontsHook;

    /// <param name="reset">Makes what was made from the fonts again.</param>
    /// <param name="state">What the parts share.</param>
    public FontLifecycleHooks(Action<ResetScope> reset, FrameState state)
    {
        this._reset = reset;
        this._state = state;
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
        this.FontsChanged("Refreshing the copies of a rebuilt font failed");
        return result;
    }

    private int UnloadFontsDetour(AtkFontManager* manager)
    {
        var result = this._unloadFontsHook.Original(manager);
        this.FontsChanged("Letting go of the unloaded fonts failed");
        return result;
    }

    // After the original, so that copies borrow what the game left, not what it was about to free.
    private void FontsChanged(string failure)
    {
        try
        {
            this._reset(ResetScope.SizedFonts);
            this._state.GameFontChanged();
        }
        catch (Exception ex)
        {
            this._state.Fail(ex, failure);
        }
    }
}
