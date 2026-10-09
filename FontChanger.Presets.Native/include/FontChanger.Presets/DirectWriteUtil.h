#pragma once

#include <initializer_list>
#include <string>
#include <string_view>
#include <vector>

#include "FontChanger.FixedSizeFont/directwrite_fixed_size_font.h"

namespace FontChanger::DirectWriteUtil {
	// Returns an OpenType tag as DirectWrite has it (DWRITE_MAKE_OPENTYPE_TAG): its first character in the lowest byte;
	// tags shorter than 4 characters are padded with spaces.
	[[nodiscard]] uint32_t StringToTag(std::string_view tag);

	// Returns the 4 characters of a tag of StringToTag.
	[[nodiscard]] std::string TagToString(uint32_t tag);

	// Returns the string of the first of the locales that the strings have, or else the first string; empty if none.
	[[nodiscard]] std::wstring GetLocalizedString(IDWriteLocalizedStrings* strings, std::initializer_list<const wchar_t*> locales);

	// Returns the name of the family in English, which lookups name families by.
	[[nodiscard]] std::wstring GetEnglishFamilyName(IDWriteFontFamily* family);

	// Returns the ranges of the axes of a variable font; empty if the font is not variable.
	[[nodiscard]] std::vector<DWRITE_FONT_AXIS_RANGE> GetFontAxisRanges(IDWriteFont* font);
}
