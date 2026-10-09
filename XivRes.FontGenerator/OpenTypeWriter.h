#pragma once

namespace App::OpenTypeWriter {
	// A font file that a font of a merged font draws from, whose layout rules (GSUB, GPOS, and GDEF) go into the written
	// font with the glyphs that they reach; see OpenTypeLayout.h for how the rules of fonts are kept apart.
	struct LayoutSource {
		// The font file, the index of the face in it, and the axis values of the instance, keyed by hb_tag_t.
		std::shared_ptr<const std::vector<uint8_t>> FileData;
		int FaceIndex = 0;
		std::map<uint32_t, float> AxisValues;

		// Draws the glyphs of the file by their indices, as the font does.
		std::shared_ptr<const xivres::fontgen::fixed_size_font> Font;

		// Transformation of the glyphs on screen, where y grows downwards: x' = M11 x + M12 y, y' = M21 x + M22 y. M21 must
		// be 0, and M11 and M22 positive, so that positions stay horizontal.
		xivres::fontgen::font_render_transformation_matrix ScreenMatrix{1.f, 0.f, 0.f, 1.f};

		// In pixels, as the wrapping font applies them.
		int LetterSpacing = 0;
		int HorizontalOffset = 0;
		int BaselineShift = 0;
		std::map<char32_t, char32_t> CodepointReplacements;

		// OpenType features that the user set, keyed by hb_tag_t; 0 turns a feature off.
		std::map<uint32_t, uint32_t> Features;

		// BCP 47 language tag of the text, such as "ja"; empty if unspecified.
		std::string Language;

		// Fonts whose sources have the same key draw alike, and share their glyphs and rules.
		std::string Key;
	};

	struct Options {
		std::string FamilyName;
		std::string SubfamilyName = "Regular";
		std::string Version = "Version 1.000";

		// usWeightClass and usWidthClass of the OS/2 table.
		uint16_t WeightClass = 400;
		uint16_t WidthClass = 5;

		// Glyphs without outlines, such as those drawn from bitmaps, are made of the squares of their pixels whose coverage
		// is at least this.
		uint8_t PixelThreshold = 128;

		// For a merged font, the layout sources of its fonts by their indices; null or missing for fonts whose glyphs are
		// written one per codepoint, without rules other than kerning pairs.
		std::vector<std::shared_ptr<const LayoutSource>> LayoutSources;
	};

	// Writes a font as an OpenType font with CFF outlines, scaled so that the size of the font is an em: the glyphs of
	// all codepoints with their outlines, the advance widths, the ascent and the line height, and the kerning pairs as
	// the 'kern' feature. Codepoints of fonts of a merged font with layout sources are written with all the glyphs and
	// rules that the sources reach from them instead. progress is called with the number of glyphs done and the total,
	// and may throw to cancel.
	[[nodiscard]] std::vector<uint8_t> Write(const xivres::fontgen::fixed_size_font& font, const Options& options, const std::function<void(size_t, size_t)>& progress = {});
}
