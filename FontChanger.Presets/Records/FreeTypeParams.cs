namespace FontChanger.Presets.Records;

/// <summary>FT_LOAD_* flags and an FT_Render_Mode.</summary>
public readonly record struct FreeTypeParams(int LoadFlags, int RenderMode)
{
    public static readonly FreeTypeParams Default = new(0, 1);
}
