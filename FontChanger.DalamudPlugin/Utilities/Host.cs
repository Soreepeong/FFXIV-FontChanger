using System;

using TerraFX.Interop.DirectX;

namespace CustomFonts;

/// <summary>
/// What the font replacement needs from what loads it into the game (Dalamud here; XivAlexander for a port): the game's
/// executable, hooks, textures the game can bind, the folder of its own files, and a log. Nothing else of the host is
/// used outside the plugin's UI.
/// </summary>
internal interface IHost
{
    IHostLog Log { get; }

    /// <summary>Gets the path of the game's executable.</summary>
    string GameFileName { get; }

    /// <summary>Gets where the game's executable is loaded.</summary>
    nint GameBaseAddress { get; }

    /// <summary>Gets the folder of the plugin's own files (native libraries).</summary>
    string AssemblyDirectory { get; }

    /// <summary>Makes a hook of a game function, disabled until <see cref="IHostHook{T}.Enable"/>.</summary>
    IHostHook<T> Hook<T>(nint address, T detour)
        where T : Delegate;

    /// <summary>Makes a B8G8R8A8 texture the game can bind (a Kernel::Texture), on the game's device.</summary>
    IHostTexture CreateTexture(int width, int height, string name);
}

/// <summary>A hook of a game function; disposing it removes it.</summary>
internal interface IHostHook<out T> : IDisposable
    where T : Delegate
{
    /// <summary>Gets the original function.</summary>
    T Original { get; }

    void Enable();
}

/// <summary>A texture the game can bind; disposing it releases it once the game is done with it.</summary>
internal unsafe interface IHostTexture : IDisposable
{
    /// <summary>Gets the texture as a Kernel::Texture*.</summary>
    nint Kernel { get; }

    /// <summary>Gets its D3D11 resource, to update its contents.</summary>
    ID3D11Resource* Resource { get; }
}

/// <summary>A log taking message templates, as Dalamud's does.</summary>
internal interface IHostLog
{
    void Debug(string template, params object?[] values);

    void Information(string template, params object?[] values);

    void Warning(string template, params object?[] values);

    void Warning(Exception? exception, string template, params object?[] values);

    void Error(Exception? exception, string template, params object?[] values);
}

/// <summary>The host the plugin was loaded by; set once at startup, before anything that uses it is made.</summary>
internal static class Host
{
    public static IHost Current { get; set; } = null!;

    public static IHostLog Log => Current.Log;
}
