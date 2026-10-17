using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Text;

/// <summary>
/// An IDWriteTextAnalysisSource of a text, left to right, in one locale, for the analyzer and the font fallback. It
/// lives where it is declared (a local); <see cref="Get"/> gives the interface, valid while the text is pinned.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TextAnalysisSource
{
    private void* _vtable;
    private delegate* unmanaged<void*, Guid*, void**, int> _queryInterface;
    private delegate* unmanaged<void*, uint> _addRef;
    private delegate* unmanaged<void*, uint> _release;
    private delegate* unmanaged<void*, uint, char**, uint*, int> _getTextAtPosition;
    private delegate* unmanaged<void*, uint, char**, uint*, int> _getTextBeforePosition;
    private delegate* unmanaged<void*, DWRITE_READING_DIRECTION> _getParagraphReadingDirection;
    private delegate* unmanaged<void*, uint, uint*, char**, int> _getLocaleName;
    private delegate* unmanaged<void*, uint, uint*, IDWriteNumberSubstitution**, int> _getNumberSubstitution;
    private char* _text;
    private uint _length;
    private char* _locale;

    /// <param name="text">The text, pinned while the source is used.</param>
    /// <param name="length">Its length in code units.</param>
    /// <param name="locale">The locale name, zero-terminated and pinned while the source is used.</param>
    public TextAnalysisSource(char* text, uint length, char* locale)
    {
        this._queryInterface = &QueryInterfaceImpl;
        this._addRef = &AddRefImpl;
        this._release = &ReleaseImpl;
        this._getTextAtPosition = &GetTextAtPositionImpl;
        this._getTextBeforePosition = &GetTextBeforePositionImpl;
        this._getParagraphReadingDirection = &GetParagraphReadingDirectionImpl;
        this._getLocaleName = &GetLocaleNameImpl;
        this._getNumberSubstitution = &GetNumberSubstitutionImpl;
        this._text = text;
        this._length = length;
        this._locale = locale;
    }

    /// <summary>Gets the interface; the source must not move while it is used.</summary>
    public IDWriteTextAnalysisSource* Get()
    {
        // The vtable is the function pointers right after it.
        var self = (void**)Unsafe.AsPointer(ref this);
        *self = self + 1;
        return (IDWriteTextAnalysisSource*)self;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceImpl(void* self, Guid* riid, void** ppv)
    {
        if (*riid == IID.IID_IUnknown || *riid == IID.IID_IDWriteTextAnalysisSource)
        {
            *ppv = self;
            return S.S_OK;
        }

        *ppv = null;
        return E.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRefImpl(void* self) => 1;

    [UnmanagedCallersOnly]
    private static uint ReleaseImpl(void* self) => 1;

    [UnmanagedCallersOnly]
    private static int GetTextAtPositionImpl(void* self, uint position, char** text, uint* length)
    {
        var s = (TextAnalysisSource*)self;
        var inside = position < s->_length;
        *text = inside ? s->_text + position : null;
        *length = inside ? s->_length - position : 0;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetTextBeforePositionImpl(void* self, uint position, char** text, uint* length)
    {
        var s = (TextAnalysisSource*)self;
        var inside = position != 0 && position <= s->_length;
        *text = inside ? s->_text : null;
        *length = inside ? position : 0;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static DWRITE_READING_DIRECTION GetParagraphReadingDirectionImpl(void* self) =>
        DWRITE_READING_DIRECTION.DWRITE_READING_DIRECTION_LEFT_TO_RIGHT;

    [UnmanagedCallersOnly]
    private static int GetLocaleNameImpl(void* self, uint position, uint* length, char** locale)
    {
        var s = (TextAnalysisSource*)self;
        *length = position < s->_length ? s->_length - position : 0;
        *locale = s->_locale;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetNumberSubstitutionImpl(void* self, uint position, uint* length, IDWriteNumberSubstitution** substitution)
    {
        var s = (TextAnalysisSource*)self;
        *length = position < s->_length ? s->_length - position : 0;
        *substitution = null;
        return S.S_OK;
    }
}
