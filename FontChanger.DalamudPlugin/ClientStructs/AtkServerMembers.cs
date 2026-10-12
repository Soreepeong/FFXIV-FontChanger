// What the plugin needs of AtkServer that FFXIVClientStructs doesn't have, written as it would be there, to be contributed:
// AtkServer members (FFXIV/Component/GUI/AtkServer.cs) and the state AtkServer.SetDrawState takes.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CustomFonts;

/// <summary>AtkServer members FFXIVClientStructs lacks.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x8680)]
internal unsafe struct AtkServerMembers
{
    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void SetDrawState(AtkServerDrawState* state, byte textureDivisor)</c>: stages a batch of UI
    /// draw commands' blend state, texture, and <c>g_TextureParam</c> (the texture's size over the divisor). ProcessUICommands
    /// calls it when those change, not when only the shader pass does.
    /// </summary>
    public const string SetDrawStateSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 20 65 48 8B 04 25 58 00 00 00 4C 8B F2 44 8B 0D ?? ?? ?? ?? 48 8B D9 41 0F B6 E8";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void BindShaders(AtkServerShaderPass pass, bool alternate)</c>: binds the pass's vertex
    /// shader and its pixel shader, or PrimitiveUIMaskPS if <c>alternate</c> (a masked draw).
    /// </summary>
    public const string BindShadersSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 65 48 8B 04 25 58 00 00 00 41 0F B6 F0 44 8B 0D ?? ?? ?? ?? 48 8B D9 8B EA";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>bool LoadShaders()</c>: loads the UI's shaders, making their Kernel shaders anew (at
    /// startup, and when the UI's shaders and fonts are reloaded).
    /// </summary>
    public const string LoadShadersSignature = "48 89 5C 24 18 48 89 7C 24 20 55 48 8B EC 48 83 EC 30 45 33 C0 48 8B D9 48 8D 4D 18";

    /// <summary>Set while drawing into a render texture (nameplate bakes) instead of the back buffer.</summary>
    [FieldOffset(0x8670)] public bool IsRenderingToTexture;
}

/// <summary>The shader pairs of AtkServer (VertexShaders at 0xE8, PixelShaders at 0x130) by pass.</summary>
internal enum AtkServerShaderPass
{
    PrimitiveUI = 0,
    PrimitiveUINaviMap = 1,
    PrimitiveUIAreaMap = 2,
    PrimitiveUICharaView = 3,
    Font = 4,
    FontEdge = 5,
    FontGlare = 6,
    FontHighlight = 7,
    FontEmboss = 8,
}

/// <summary>What AtkServer.SetDrawState stages for a batch of UI draw commands.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
internal unsafe struct AtkServerDrawState
{
    [FieldOffset(0x00)] public PackedBlendStateDesc BlendState;
    [FieldOffset(0x08)] public Texture* Texture; // null for the white or black texture
    [FieldOffset(0x10)] public uint Unk10;
    [FieldOffset(0x14)] public uint Flags;

    /// <summary>Gets or sets the blend state's 32 bits.</summary>
    public uint BlendStateValue
    {
        readonly get => Unsafe.As<PackedBlendStateDesc, uint>(ref Unsafe.AsRef(in this.BlendState));
        set => Unsafe.As<PackedBlendStateDesc, uint>(ref this.BlendState) = value;
    }
}
