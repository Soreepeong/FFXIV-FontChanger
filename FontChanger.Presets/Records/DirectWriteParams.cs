namespace FontChanger.Presets.Records;

public readonly record struct DirectWriteParams(int RenderMode, int MeasureMode, int GridFitMode)
{
    public static readonly DirectWriteParams Default = new(4, 1, 2);

    public static string DescribeRenderMode(int mode) => mode switch
    {
        0 => "DEFAULT",
        1 => "ALIASED",
        2 => "GDI_CLASSIC",
        3 => "GDI_NATURAL",
        4 => "NATURAL",
        5 => "NATURAL_SYMMETRIC",
        6 => "OUTLINE",
        7 => "NATURAL_SYMMETRIC_DOWNSAMPLED",
        _ => $"unknown({mode})",
    };

    public static string DescribeMeasureMode(int mode) => mode switch
    {
        0 => "NATURAL",
        1 => "GDI_CLASSIC",
        2 => "GDI_NATURAL",
        _ => $"unknown({mode})",
    };

    public static string DescribeGridFitMode(int mode) => mode switch
    {
        0 => "DEFAULT",
        1 => "DISABLED",
        2 => "ENABLED",
        _ => $"unknown({mode})",
    };

    public override string ToString() =>
        $"{nameof(DirectWriteParams)}({nameof(RenderMode)}={DescribeRenderMode(RenderMode)}, {nameof(MeasureMode)}={DescribeMeasureMode(MeasureMode)}, {nameof(GridFitMode)}={DescribeGridFitMode(GridFitMode)})";
}
