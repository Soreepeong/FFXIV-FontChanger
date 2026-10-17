using System;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// The (first) file of a font face: its loader and reference key, which tell its file apart from others'. Valid until
/// disposed; <see cref="Loader"/> is null if the face has no file.
/// </summary>
internal unsafe ref struct FontFileKey : IDisposable
{
    private MyComPtr<IDWriteFontFile> _file;
    private MyComPtr<IDWriteFontFileLoader> _loader;

    public readonly IDWriteFontFileLoader* Loader => this._loader.Value;

    public ReadOnlySpan<byte> Key { get; private set; }

    public static FontFileKey Of(IDWriteFontFace5* face)
    {
        var res = default(FontFileKey);
        uint count = 1;
        void* key;
        uint size;
        if (face->GetFiles(&count, res._file.GetAddressOf()).FAILED
            || !res._file
            || res._file.Value->GetReferenceKey(&key, &size).FAILED
            || res._file.Value->GetLoader(res._loader.GetAddressOf()).FAILED)
        {
            res.Dispose();
            return default;
        }

        res.Key = new(key, (int)size);
        return res;
    }

    public void Dispose()
    {
        this._loader.Dispose();
        this._file.Dispose();
    }
}
