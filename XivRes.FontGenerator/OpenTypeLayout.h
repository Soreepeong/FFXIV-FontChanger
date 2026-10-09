#pragma once

// Merges the layout tables (GSUB, GPOS, and GDEF) of the fonts that a merged font takes glyphs from. Each source keeps
// its rules to its own glyphs, so that text shapes as it would if each run of glyphs of a source were shaped with that
// font alone:
// - no rule spans the glyphs of two sources; glyphs of other sources are given a class that no rule uses, where they
//   would otherwise fall in class 0;
// - each source contributes to a script and language system what it would use for them, falling back to its 'DFLT'
//   script and then to its 'latn' script, and to the language system that the user picked for it;
// - features that the user turned on for a source and that are not on by default apply as 'rclt', with the alternates
//   that the user picked.
namespace App::OpenTypeLayout {
	// Maps positions of a source font into the merged font, in font units with y growing upwards:
	// x' = (XX x + XY y) Scale, y' = YY y Scale.
	struct PositionTransform {
		float XX = 1.f;
		float XY = 0.f;
		float YY = 1.f;
		float Scale = 1.f;
	};

	// Layout tables of a source font, subset to the glyphs that the merged font takes from it, which are numbered as in
	// the merged font, from FirstGlyph to LastGlyph.
	struct Source {
		std::vector<uint8_t> Gsub;
		std::vector<uint8_t> Gpos;
		std::vector<uint8_t> Gdef;
		uint16_t FirstGlyph = 0;
		uint16_t LastGlyph = 0;
		PositionTransform Transform;

		// Features that the user turned on, keyed by OpenType tags as hb_tag_t; values above 1 pick an alternate.
		std::map<uint32_t, uint32_t> EnabledFeatures;

		// Language system tags of the language that the user picked, as hb_tag_t, in the order of preference.
		std::vector<uint32_t> LanguageTags;
	};

	struct Input {
		uint32_t GlyphCount = 0;
		std::vector<Source> Sources;

		// Kerning between glyphs that come from no source, as a 'kern' feature for all scripts.
		std::map<uint16_t, std::map<uint16_t, int>> Kerning;

		// Classes of glyphs for GDEF, for the glyphs that no source classifies: 1 for bases, 2 for ligatures, and 3 for marks.
		std::map<uint16_t, uint16_t> GlyphClasses;
	};

	struct Output {
		std::vector<uint8_t> Gsub;
		std::vector<uint8_t> Gpos;
		std::vector<uint8_t> Gdef;

		// Largest number of glyphs that a rule looks at, for usMaxContext of OS/2.
		uint16_t MaxContext = 0;
	};

	[[nodiscard]] Output Merge(const Input& input);

	// Returns whether shapers apply a feature by default, for some scripts at least.
	[[nodiscard]] bool IsDefaultFeature(uint32_t tag);
}
