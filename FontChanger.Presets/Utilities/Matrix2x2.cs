using System.Diagnostics.CodeAnalysis;

namespace FontChanger.Presets.Utilities;

[SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Matrix3x2, Matrix4x4")]
public readonly record struct Matrix2x2(float M11, float M12, float M21, float M22)
{
    public static readonly Matrix2x2 Identity = new(1, 0, 0, 1);

    public static Matrix2x2 operator *(Matrix2x2 a, Matrix2x2 b) => new(
        (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M21 * b.M12) + (a.M22 * b.M22));

    public static Matrix2x2 CreateScale(float xScale, float yScale) => new(xScale, 0, 0, yScale);
}
