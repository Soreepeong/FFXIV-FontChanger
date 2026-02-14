#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <algorithm>
#include <cmath>
#include <exception>
#include <iostream>
#include <ranges>
#include <string>
#include <string_view>

#include <Windows.h>
#include <windowsx.h>

#include <comdef.h>
#include <CommCtrl.h>
#include <dwrite.h>
#include <PathCch.h>
#include <Psapi.h>
#include <shellapi.h>
#include <ShellScalingApi.h>
#include <ShObjIdl.h>
#include <ShlGuid.h>
#include <wincrypt.h>

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

#include "xivres.fontgen/directwrite_fixed_size_font.h"
#include "xivres.fontgen/fontdata_fixed_size_font.h"
#include "xivres.fontgen/fontdata_packer.h"
#include "xivres.fontgen/freetype_fixed_size_font.h"
#include "xivres.fontgen/merged_fixed_size_font.h"
#include "xivres.fontgen/text_measurer.h"
#include "xivres.fontgen/wrapping_fixed_size_font.h"
#include "xivres/fontdata.h"
#include "xivres/installation.h"
#include "xivres/packed_stream.standard.h"
#include "xivres/packed_stream.texture.h"
#include "xivres/texture.h"
#include "xivres/texture.mipmap_stream.h"
#include "xivres/texture.preview.h"
#include "xivres/unpacked_stream.h"
#include "xivres/util.on_dtor.h"
#include "xivres/util.pixel_formats.h"

#include "MiscUtil.h"

extern HINSTANCE g_hInstance;
extern struct FontGeneratorConfig g_config;
extern WORD g_langId;
extern std::wstring g_localeName;

_COM_SMARTPTR_TYPEDEF(IFileSaveDialog, __uuidof(IFileSaveDialog));
_COM_SMARTPTR_TYPEDEF(IFileOpenDialog, __uuidof(IFileOpenDialog));
_COM_SMARTPTR_TYPEDEF(IShellItem, __uuidof(IShellItem));
_COM_SMARTPTR_TYPEDEF(IShellItemArray, __uuidof(IShellItemArray));
_COM_SMARTPTR_TYPEDEF(IDWriteFont, __uuidof(IDWriteFont));
_COM_SMARTPTR_TYPEDEF(IDWriteFactory, __uuidof(IDWriteFactory));

inline std::wstring GetWindowString(HWND hwnd, bool trim = false) {
	std::wstring buf(GetWindowTextLengthW(hwnd) + static_cast<size_t>(1), L'\0');
	buf.resize(GetWindowTextW(hwnd, buf.data(), static_cast<int>(buf.size())));
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

template<typename T, typename = std::enable_if_t<(sizeof(T) <= sizeof(LPARAM))>>
inline void SetComboboxContent(HWND hCombo, T currentValue, std::initializer_list<std::pair<T, UINT>> args) {
	ComboBox_ResetContent(hCombo);
	std::wstring text;
	for (const auto& [val, resId] : args) {
		text = GetStringResource(resId);
		const auto index = ComboBox_AddString(hCombo, text.c_str());
		ComboBox_SetItemData(hCombo, index, val);
		if (currentValue == val)
			ComboBox_SetCurSel(hCombo, index);
	}
}

template<typename T, typename = std::enable_if_t<(sizeof(T) <= sizeof(LPARAM))>>
inline T GetComboboxSelData(HWND hCombo) {
	return static_cast<T>(ComboBox_GetItemData(hCombo, ComboBox_GetCurSel(hCombo)));
}

class WException {
	std::wstring m_msg;

public:
	WException(std::wstring msg) : m_msg(msg) {}

	const std::wstring& what() const {
		return m_msg;
	}
};
