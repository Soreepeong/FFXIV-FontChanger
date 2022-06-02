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
#include <PathCch.h>
#include <Psapi.h>

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

// https://github.com/Nomade040/nmd/blob/master/nmd_assembly.h
#include "nmd_assembly.h"

#include "xivres/packed_stream.standard.h"
#include "xivres/packed_stream.texture.h"
#include "xivres/fontdata.h"
#include "xivres/installation.h"
#include "xivres/texture.h"
#include "xivres/texture.mipmap_stream.h"
#include "xivres/unpacked_stream.h"
#include "xivres/util.pixel_formats.h"
#include "xivres/util.on_dtor.h"
#include "xivres.fontgen/WrappingFixedSizeFont.h"
#include "xivres.fontgen/DirectWriteFixedSizeFont.h"
#include "xivres.fontgen/FontdataPacker.h"
#include "xivres.fontgen/FreeTypeFixedSizeFont.h"
#include "xivres.fontgen/GameFontdataFixedSizeFont.h"
#include "xivres.fontgen/MergedFixedSizeFont.h"
#include "xivres.fontgen/TextMeasurer.h"
#include "xivres.fontgen/WrappingFixedSizeFont.h"
#include "xivres/texture.preview.h"

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

HRESULT SuccessOrThrow(HRESULT hr, std::initializer_list<HRESULT> acceptables = {});
