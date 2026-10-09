#pragma once

#include <functional>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "FontChanger.Presets/Structs.h"

// Makes faces of the game's fonts drawn with a font of the system: sized so that its capitals (or digits, for the
// families of only digits) are as tall as the game's, placed as the game places its glyphs, and drawn with DirectWrite, in
// natural mode at sizes the font has bitmaps of, and in natural symmetric mode at others. Faces of AXIS also get the
// game's symbols from the Lodestone font if installed, and drawn in the font by glyph merging, as are the IME indicators,
// with Windows' fonts for scripts it lacks. The C# twin is FontChanger.Presets/FaceFromFont.cs.
namespace FontChanger::FaceFromFont {
	// Ink box and advance of a glyph over the em; Top and Bottom are above the baseline.
	struct GlyphBox {
		float Left;
		float Right;
		float Advance;
		float Top;
		float Bottom;
	};

	// What the generator needs to know of a font.
	class FontInfo {
		IDWriteFontFacePtr m_face;
		float m_unitsPerEm = 1;
		std::set<int> m_bitmapSizes;

	public:
		explicit FontInfo(IDWriteFont* font);

		// Opens the font of a family that DirectWrite matches to a weight, stretch and style; nullopt if not installed.
		static std::optional<FontInfo> Open(const std::string& name, DWRITE_FONT_WEIGHT weight = DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STRETCH stretch = DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE style = DWRITE_FONT_STYLE_NORMAL);

		// Returns the glyph of a codepoint; nullopt if the font lacks it.
		[[nodiscard]] std::optional<GlyphBox> GetGlyph(char32_t codepoint) const;

		// Returns whether the font has bitmaps of a size in pixels per em.
		[[nodiscard]] bool HasBitmapOf(int ppem) const;

		// Returns whether the font's OpenType layout (GSUB or GPOS) has a feature.
		[[nodiscard]] bool HasFeature(std::string_view tag) const;
	};

	// Returns the font of a lookup's name, weight, stretch and style, or nullptr if it isn't installed.
	using FontOpener = std::function<const FontInfo*(const Structs::LookupStruct& lookup)>;

	// Returns the family and the size of a face of the game fonts by its name, as in the definitions of the game fonts;
	// Jupiter_45 and Jupiter_90, which only have digits and a few symbols, are of the family JupiterN.
	std::optional<std::pair<std::string_view, float>> GetGameFontFamilyAndSize(std::string_view faceName);

	// Returns the size that the game's own font is drawn at in the face: that of its element of the game font, or if it
	// has none, the size of the game font by its name.
	std::optional<float> GetGameFontSize(const Structs::Face& face);

	// Returns the lookup of a font that GDI names (as ChooseFont does), by the family's name in DirectWrite: GDI names some
	// faces as families of their own ("Franklin Gothic Medium").
	Structs::LookupStruct GetLookupFromLogFont(const LOGFONTW& logFont);

	// Makes the elements that draw a face of a game font family at a size with a font, to follow the game's own element.
	// With monospacedDigits, the digits are the font's tabular figures (tnum), or, if it has none, put each in a cell as
	// wide as its 0.
	std::vector<std::unique_ptr<Structs::FaceElement>> MakeElements(
		std::string_view family, float size, const Structs::LookupStruct& chosen, const FontInfo& font, const FontOpener& open, bool monospacedDigits = true);

	// Makes the elements of every face of a game font family in the font set, each at its size.
	std::vector<std::pair<Structs::Face*, std::vector<std::unique_ptr<Structs::FaceElement>>>> MakeFamilyElements(
		const Structs::FontSet& fontSet, std::string_view family, const Structs::LookupStruct& chosen);
}
