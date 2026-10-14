using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Utilities;

[SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Matrix3x2, Matrix4x4")]
public readonly record struct Matrix2x2(float M11, float M12, float M21, float M22)
{
    /// <inheritdoc cref="Matrix4x4.Identity"/>
    public static readonly Matrix2x2 Identity = new(1, 0, 0, 1);

    /// <inheritdoc cref="Matrix4x4.IsIdentity"/>
    public bool IsIdentity => this == Identity;

    /// <inheritdoc cref="Matrix4x4.GetDeterminant"/>
    public float GetDeterminant() => (this.M11 * this.M22) - (this.M12 * this.M21);

    /// <inheritdoc cref="Matrix4x4.op_Addition(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 operator +(Matrix2x2 a, Matrix2x2 b) => new(a.M11 + b.M11, a.M12 + b.M12, a.M21 + b.M21, a.M22 + b.M22);

    /// <inheritdoc cref="Matrix4x4.op_Subtraction(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 operator -(Matrix2x2 a, Matrix2x2 b) => new(a.M11 - b.M11, a.M12 - b.M12, a.M21 - b.M21, a.M22 - b.M22);

    /// <inheritdoc cref="Matrix4x4.op_UnaryNegation(Matrix4x4)"/>
    public static Matrix2x2 operator -(Matrix2x2 a) => new(-a.M11, -a.M12, -a.M21, -a.M22);

    /// <inheritdoc cref="Matrix4x4.op_Multiply(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 operator *(Matrix2x2 a, Matrix2x2 b) => new(
        (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M21 * b.M12) + (a.M22 * b.M22));

    /// <inheritdoc cref="Matrix4x4.op_Multiply(Matrix4x4, float)"/>
    public static Matrix2x2 operator *(Matrix2x2 a, float b) => new(a.M11 * b, a.M12 * b, a.M21 * b, a.M22 * b);

    /// <inheritdoc cref="Matrix4x4.op_Multiply(Matrix4x4, float)"/>
    public static Matrix2x2 operator *(float a, Matrix2x2 b) => b * a;

    /// <inheritdoc cref="Transform(Vector2, Matrix2x2)"/>
    public static Vector2 operator *(Matrix2x2 matrix, Vector2 value) => Transform(value, matrix);

    /// <inheritdoc cref="Matrix4x4.Add(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 Add(Matrix2x2 a, Matrix2x2 b) => a + b;

    /// <inheritdoc cref="Matrix4x4.Subtract(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 Subtract(Matrix2x2 a, Matrix2x2 b) => a - b;

    /// <inheritdoc cref="Matrix4x4.Negate(Matrix4x4)"/>
    public static Matrix2x2 Negate(Matrix2x2 value) => -value;

    /// <inheritdoc cref="Matrix4x4.Multiply(Matrix4x4, Matrix4x4)"/>
    public static Matrix2x2 Multiply(Matrix2x2 a, Matrix2x2 b) => a * b;

    /// <inheritdoc cref="Matrix4x4.Multiply(Matrix4x4, float)"/>
    public static Matrix2x2 Multiply(Matrix2x2 a, float b) => a * b;

    /// <inheritdoc cref="Matrix4x4.Lerp(Matrix4x4, Matrix4x4, float)"/>
    public static Matrix2x2 Lerp(Matrix2x2 a, Matrix2x2 b, float amount) => new(
        a.M11 + ((b.M11 - a.M11) * amount),
        a.M12 + ((b.M12 - a.M12) * amount),
        a.M21 + ((b.M21 - a.M21) * amount),
        a.M22 + ((b.M22 - a.M22) * amount));

    /// <inheritdoc cref="Matrix4x4.Transpose(Matrix4x4)"/>
    public static Matrix2x2 Transpose(Matrix2x2 value) => new(value.M11, value.M21, value.M12, value.M22);

    /// <inheritdoc cref="Matrix4x4.Invert(Matrix4x4, out Matrix4x4)"/>
    public static bool Invert(Matrix2x2 matrix, out Matrix2x2 result)
    {
        var det = matrix.GetDeterminant();
        if (MathF.Abs(det) < float.Epsilon)
        {
            result = new(float.NaN, float.NaN, float.NaN, float.NaN);
            return false;
        }

        var invDet = 1 / det;
        result = new(matrix.M22 * invDet, -matrix.M12 * invDet, -matrix.M21 * invDet, matrix.M11 * invDet);
        return true;
    }

    /// <inheritdoc cref="Vector2.Transform(Vector2, Matrix4x4)"/>
    public static Vector2 Transform(Vector2 position, Matrix2x2 matrix) => new(
        (matrix.M11 * position.X) + (matrix.M12 * position.Y),
        (matrix.M21 * position.X) + (matrix.M22 * position.Y));

    /// <inheritdoc cref="Matrix3x2.CreateScale(float, float)"/>
    public static Matrix2x2 CreateScale(float xScale, float yScale) => new(xScale, 0, 0, yScale);

    /// <inheritdoc cref="Matrix3x2.CreateScale(float)"/>
    public static Matrix2x2 CreateScale(float scale) => new(scale, 0, 0, scale);

    /// <inheritdoc cref="Matrix3x2.CreateScale(Vector2)"/>
    public static Matrix2x2 CreateScale(Vector2 scales) => new(scales.X, 0, 0, scales.Y);

    /// <inheritdoc cref="Matrix3x2.CreateRotation(float)"/>
    public static Matrix2x2 CreateRotation(float radians)
    {
        var (s, c) = MathF.SinCos(radians);
        return new(c, s, -s, c);
    }

    /// <inheritdoc cref="Matrix3x2.CreateSkew(float, float)"/>
    /// <remarks>
    /// A positive <paramref name="radiansX"/> makes the tops of glyphs lean to the right, as italics do, and a positive
    /// <paramref name="radiansY"/> moves points to the right of the origin down.
    /// </remarks>
    public static Matrix2x2 CreateSkew(float radiansX, float radiansY) => new(1, -MathF.Tan(radiansX), MathF.Tan(radiansY), 1);

    /// <summary>Makes a transformation from FontChanger's components: rotation * skew * scale.</summary>
    public static Matrix2x2 FromComponents(float scaleX, float scaleY, float skewDegrees, float rotationDegrees) =>
        CreateRotation(rotationDegrees * MathF.PI / 180) *
        CreateSkew(skewDegrees * MathF.PI / 180, 0) *
        CreateScale(scaleX, scaleY);

    /// <summary>Gets the transformation a renderer's matrix (as FontChanger stored them before) makes on screen.</summary>
    public static Matrix2x2 FromRendererMatrix(
        Renderer renderer,
        float m11,
        float m12,
        float m21,
        float m22) => renderer switch
    {
        // DirectWrite transforms row vectors; FreeType column vectors with y growing upwards.
        Renderer.DirectWrite => new(m11, m21, m12, m22),
        Renderer.GlyphImages => new(m11, m12, m21, m22),
        _ => new(m11, -m12, -m21, m22),
    };

    /// <inheritdoc cref="Matrix4x4.ToString"/>
    public override string ToString() => $"{{ {{M11:{this.M11} M12:{this.M12}}} {{M21:{this.M21} M22:{this.M22}}} }}";
}
