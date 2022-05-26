#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <cmath>
#include <iostream>
#include <ranges>

#include <Windows.h>
#include <windowsx.h>
#include <CommCtrl.h>
#include <ShObjIdl.h>
#include <comdef.h>
#include <dwrite.h>
#include <shellapi.h>

#include <exprtk.hpp>

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_BITMAP_H
#include FT_OUTLINE_H 
#include FT_GLYPH_H

#include <zlib.h>
#include <minizip/zip.h>
#include <minizip/iowin32.h>

#include <nlohmann/json.hpp>

#include "XivRes/BinaryPackedFileStream.h"
#include "XivRes/FontdataStream.h"
#include "XivRes/GameReader.h"
#include "XivRes/MipmapStream.h"
#include "XivRes/PackedFileUnpackingStream.h"
#include "XivRes/PixelFormats.h"
#include "XivRes/TexturePackedFileStream.h"
#include "XivRes/TextureStream.h"
#include "XivRes/FontGenerator/WrappingFixedSizeFont.h"
#include "XivRes/FontGenerator/DirectWriteFixedSizeFont.h"
#include "XivRes/FontGenerator/FontdataPacker.h"
#include "XivRes/FontGenerator/FreeTypeFixedSizeFont.h"
#include "XivRes/FontGenerator/GameFontdataFixedSizeFont.h"
#include "XivRes/FontGenerator/MergedFixedSizeFont.h"
#include "XivRes/FontGenerator/TextMeasurer.h"
#include "XivRes/FontGenerator/WrappingFixedSizeFont.h"
#include "XivRes/Internal/TexturePreview.Windows.h"

extern HINSTANCE g_hInstance;

_COM_SMARTPTR_TYPEDEF(IFileSaveDialog, __uuidof(IFileSaveDialog));
_COM_SMARTPTR_TYPEDEF(IFileOpenDialog, __uuidof(IFileOpenDialog));
_COM_SMARTPTR_TYPEDEF(IShellItem, __uuidof(IShellItem));
_COM_SMARTPTR_TYPEDEF(IShellItemArray, __uuidof(IShellItemArray));
_COM_SMARTPTR_TYPEDEF(IDWriteFont, __uuidof(IDWriteFont));
_COM_SMARTPTR_TYPEDEF(IDWriteFactory, __uuidof(IDWriteFactory));

inline std::wstring GetWindowString(HWND hwnd, bool trim = false) {
	std::wstring buf(GetWindowTextLengthW(hwnd) + static_cast<size_t>(1), L'\0');
	buf.resize(GetWindowTextW(hwnd, &buf[0], static_cast<int>(buf.size())));
	if (trim) {
		const auto l = buf.find_first_not_of(L"\t\r\n ");
		if (l == std::wstring::npos)
			return {};

		const auto r = buf.find_last_not_of(L"\t\r\n ");
		return buf.substr(l, r - l + 1);
	}
	return buf;
}

template<typename T>
inline T GetWindowNumber(HWND hwnd) {
	return static_cast<T>(std::wcstod(GetWindowString(hwnd, true).c_str(), nullptr));
}

template<typename T>
inline void SetWindowNumber(HWND hwnd, T v) {
	if constexpr (std::is_floating_point_v<T>)
		SetWindowTextW(hwnd, std::format(L"{:g}", v).c_str());
	else if constexpr (std::is_integral_v<T>)
		SetWindowTextW(hwnd, std::format(L"{}", v).c_str());
	else
		static_assert(!sizeof(T), "no match");
}
