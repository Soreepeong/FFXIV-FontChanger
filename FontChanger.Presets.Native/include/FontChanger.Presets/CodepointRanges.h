#pragma once

#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace FontChanger::CodepointRanges {
	// Inclusive ranges of codepoints, as in wrap_modifiers::Codepoints.
	using Ranges = std::vector<std::pair<char32_t, char32_t>>;

	// Reads ranges separated by commas or semicolons: "U+3000-30FF", "0x41~5A", "\x41:5A", "U+20", or characters as they
	// are. Returns them sorted, with overlapping and adjacent ranges merged.
	[[nodiscard]] Ranges Parse(std::wstring_view text);

	// Formats runs of consecutive codepoints as ranges, in a way that Parse reads back.
	[[nodiscard]] std::wstring Format(const std::u32string& codepoints);

	// Returns the codepoints as sorted ranges of runs of consecutive codepoints.
	[[nodiscard]] Ranges FromCodepoints(const std::set<char32_t>& codepoints);
	[[nodiscard]] Ranges FromCodepoints(const std::u32string& codepoints);

	// Returns the ranges without the codepoints in the others.
	[[nodiscard]] Ranges Subtract(const Ranges& ranges, const Ranges& others);

	[[nodiscard]] bool Contains(const Ranges& ranges, char32_t codepoint);
}
