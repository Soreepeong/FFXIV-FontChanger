namespace FontChanger.Presets.Records;

/// <summary>Values of DWRITE_RENDERING_MODE1, DWRITE_MEASURING_MODE and DWRITE_GRID_FIT_MODE.</summary>
public readonly record struct DirectWriteParams(int RenderMode, int MeasureMode, int GridFitMode)
{
    public static readonly DirectWriteParams Default = new(4, 1, 2);
}
