#pragma once

#include "FontChanger.Presets/Structs.h"

namespace App::FaceFromFont {
	// Returns the family and the size of a face of the game fonts by its name, as in the definitions of the game fonts;
	// Jupiter_45 and Jupiter_90, which only have digits and a few symbols, are of the family JupiterN.
	std::optional<std::pair<std::string_view, float>> GetGameFontFamilyAndSize(std::string_view faceName);

	// Returns the size that the game's own font is drawn at in the face: that of its element of the game font, or if it
	// has none, the size of the game font by its name.
	std::optional<float> GetGameFontSize(const Structs::Face& face);

	// Makes the elements that draw a face of a game font family at a size with a font, to follow the game's own element:
	// sized so that the font's capitals (or digits, for the families of only digits) are as tall as the game's, placed as
	// the game places its glyphs, and drawn with DirectWrite, in natural mode at sizes the font has bitmaps of, and in
	// natural symmetric mode at others. Faces of AXIS also get the game's symbols from the Lodestone font if installed,
	// and drawn in the font by glyph merging, as are the IME indicators, with Windows' fonts for scripts it lacks.
	std::vector<std::unique_ptr<Structs::FaceElement>> MakeElements(const Structs::LookupStruct& lookup, std::string_view family, float size);
}
