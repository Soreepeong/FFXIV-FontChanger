#pragma once

#include <array>
#include <string>
#include <vector>

#include "FontChanger.FixedSizeFont/glyph_merging_fixed_size_font.h"

namespace FontChanger::GlyphMergingPresets {
	struct Preset {
		std::vector<FontChanger::FixedSizeFont::glyph_merge_mapping> Mappings;

		// Codepoints of all the mappings.
		std::u32string Codepoints;
	};

	inline constexpr size_t Count = 13;

	// Glyph merging that draws the private use area glyphs of the game fonts, in groups as the font editor offers them:
	// the AM/PM marks, digits and levels, the IME indicators (as the game draws them; texts beginning with "_" are of the
	// underlined boxes), and so on; the presets of data/glyph_merge_shapes.json of FontChanger.FixedSizeFont.
	const std::array<Preset, Count>& Get();
}
