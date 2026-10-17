using System.Text;
using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

public sealed record Monospacing(
    float? Min,
    float? Max,
    MonospacingUnit Unit,
    Rune ReferenceCharacter,
    MonospacingAlignment Alignment,
    bool DropKerning = true);
