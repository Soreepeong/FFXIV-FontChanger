namespace FontChanger.DalamudPlugin.Rendering.Enums;

internal static class SubpixelOrderExtensions
{
    extension(SubpixelOrder order)
    {
        /// <summary>Gets whether the stripes are stacked vertically.</summary>
        public bool IsVertical => order is SubpixelOrder.VerticalRgb or SubpixelOrder.VerticalBgr;

        /// <summary>Gets whether blue comes first: R, G, B coverage is stored swapped from the stripes' order.</summary>
        public bool IsBlueFirst => order is SubpixelOrder.Bgr or SubpixelOrder.VerticalBgr;
    }
}
