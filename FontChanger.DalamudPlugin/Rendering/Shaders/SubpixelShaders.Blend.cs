using System.Runtime.CompilerServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

// Comparing and describing the game's packed blend states.
internal sealed partial class SubpixelShaders
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SameBlend(PackedBlendStateDesc a, PackedBlendStateDesc b) =>
        Unsafe.As<PackedBlendStateDesc, uint>(ref a) == Unsafe.As<PackedBlendStateDesc, uint>(ref b);

    private static string Describe(PackedBlendStateDesc desc) =>
        $"BlendState({(desc.BlendEnable ? "enabled" : "disabled")}" +
        $", op={Describe((D3D11_BLEND_OP)desc.BlendOp)}" +
        $"(src={Describe((D3D11_BLEND)desc.SrcBlend)}" +
        $", desc={Describe((D3D11_BLEND)desc.DestBlend)}" +
        $"), alpha={desc.BlendOpAlpha}" +
        $"(src={Describe((D3D11_BLEND)desc.SrcBlendAlpha)}" +
        $", desc={Describe((D3D11_BLEND)desc.DestBlendAlpha)}" +
        $"))";

    private static string Describe(D3D11_BLEND_OP op) => op switch
    {
        D3D11_BLEND_OP.D3D11_BLEND_OP_ADD => "ADD",
        D3D11_BLEND_OP.D3D11_BLEND_OP_SUBTRACT => "SUBTRACT",
        D3D11_BLEND_OP.D3D11_BLEND_OP_REV_SUBTRACT => "REV_SUBTRACT",
        D3D11_BLEND_OP.D3D11_BLEND_OP_MIN => "MIN",
        D3D11_BLEND_OP.D3D11_BLEND_OP_MAX => "MAX",
        _ => $"unknown(0x{(uint)op:08X})",
    };

    private static string Describe(D3D11_BLEND blend) => blend switch
    {
        D3D11_BLEND.D3D11_BLEND_ZERO => "ZERO",
        D3D11_BLEND.D3D11_BLEND_ONE => "ONE",
        D3D11_BLEND.D3D11_BLEND_SRC_COLOR => "SRC_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR => "INV_SRC_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC_ALPHA => "SRC_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA => "INV_SRC_ALPHA",
        D3D11_BLEND.D3D11_BLEND_DEST_ALPHA => "DEST_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA => "INV_DEST_ALPHA",
        D3D11_BLEND.D3D11_BLEND_DEST_COLOR => "DEST_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR => "INV_DEST_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC_ALPHA_SAT => "SRC_ALPHA_SAT",
        D3D11_BLEND.D3D11_BLEND_BLEND_FACTOR => "BLEND_FACTOR",
        D3D11_BLEND.D3D11_BLEND_INV_BLEND_FACTOR => "INV_BLEND_FACTOR",
        D3D11_BLEND.D3D11_BLEND_SRC1_COLOR => "SRC1_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_SRC1_COLOR => "INV_SRC1_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC1_ALPHA => "SRC1_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_SRC1_ALPHA => "INV_SRC1_ALPHA",
        _ => $"unknown(0x{(uint)blend:08X})",
    };
}
