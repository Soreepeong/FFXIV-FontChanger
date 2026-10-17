namespace FontChanger.DalamudPlugin.Fonts.Enums;

internal static class SubpixelOrderExtensions
{
    extension(SubpixelOrder order)
    {
        public bool IsVertical => order is SubpixelOrder.VerticalRgb or SubpixelOrder.VerticalBgr;

        public bool IsBlueFirst => order is SubpixelOrder.Bgr or SubpixelOrder.VerticalBgr;
    }
}