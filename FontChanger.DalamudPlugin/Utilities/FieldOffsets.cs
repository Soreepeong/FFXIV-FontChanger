using System.Reflection;

namespace CustomFonts;

/// <summary>
/// The offset of a field of an interop struct, read from the field's own <see cref="FieldOffsetAttribute"/>.
/// </summary>
/// <remarks>
/// The usual way to get a field's offset without an instance — <c>(nint)&amp;((T*)null)-&gt;field</c> — does not fold into a
/// constant: it reads through the null pointer and throws <see cref="NullReferenceException"/> (in Debug and in Release
/// alike, unlike the C++ trick it is modelled on). Reading the attribute is pure metadata, so it cannot fault.
/// </remarks>
internal static class FieldOffsets {
    /// <summary>Gets the offset of a field of <paramref name="type"/>, or throws if the field has no offset.</summary>
    public static nint Of(Type type, string fieldName) {
        var value = type.GetField(fieldName)?.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
        return value is { } offset
            ? offset
            : throw new InvalidOperationException($"{type.Name}.{fieldName} has no FieldOffset attribute.");
    }
}
