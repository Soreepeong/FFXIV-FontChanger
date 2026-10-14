using System.Text;
using System.Text.Json.Serialization;

using JetBrains.Annotations;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets.Records;

[JsonConverter(typeof(RuneToIntConverter))]
public sealed record Monospacing(
    float? Min,
    float? Max,
    MonospacingUnit Unit,
    Rune ReferenceCharacter,
    MonospacingAlignment Alignment,
    [UsedImplicitly] bool DropKerning = true);
