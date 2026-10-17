using System.Diagnostics.CodeAnalysis;

using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public delegate bool TryOpenFontDelegate(LookupStruct lookup, [NotNullWhen(true)] out IFontProbe? probe);