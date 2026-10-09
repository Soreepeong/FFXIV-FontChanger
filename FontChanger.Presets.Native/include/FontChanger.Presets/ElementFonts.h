#pragma once

#include <exception>
#include <filesystem>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <tuple>
#include <vector>

#include "Structs.h"

// The fonts that the elements of a preset make: the base font of an element, by its renderer, the wrapped font, and the
// merged font of a face, as FaceElement::GetBaseFont and the like return them; and how a lookup resolves to an installed
// font. What the fonts are made from is configured process-wide: the game installations, and the project directory.
namespace FontChanger::ElementFonts {
	// How a requested face is made from the closest face of the family.
	struct SynthesizedFace {
		// Slope of the oblique simulation of DirectWrite, which shears the outline by x += 0.33985 * height
		// (about 18.77 degrees), as measured from GetGlyphRunOutline of Arial 'l'; FreeType slants by the same.
		static constexpr float ObliqueSlope = 0.33985f;

		// Simulations for DirectWrite to apply; unset to keep those of the matched font, as earlier versions did.
		std::optional<DWRITE_FONT_SIMULATIONS> Simulations;

		// Weight to add by emboldening outlines, for FreeType; negative values make glyphs thinner.
		int WeightDelta = 0;

		// Whether glyphs are slanted by the matrix, for FreeType; DirectWrite slants with Simulations instead.
		bool Oblique = false;

		// Horizontal scale that makes the width of the face the requested one.
		float ScaleX = 1.f;

		// Axis values of variable fonts that give the requested properties, keyed by axis tags in DWRITE_FONT_AXIS_TAG
		// byte order; Variations of the lookup override them.
		std::map<uint32_t, float> AxisValues;

		// Returns the transformation on screen, applied before that of the element: the slant, and then the scale.
		[[nodiscard]] FontChanger::FixedSizeFont::font_render_transformation_matrix GetScreenMatrix() const;

		// Returns the emboldening for FreeType in ems.
		[[nodiscard]] float GetEmbolden() const;
	};

	// Returns the width of the stretch as a percentage of the normal width, as in usWidthClass of OS/2; 0 if undefined.
	[[nodiscard]] float GetStretchPercent(DWRITE_FONT_STRETCH stretch);

	// Returns the installed font that the lookup names, with a DirectWrite factory of its own.
	std::pair<IDWriteFactoryPtr, IDWriteFontPtr> ResolveFont(const Structs::LookupStruct& lookup);

	// Returns the font file, the index of the face in it, and the axis values of the instance if it is a variable font.
	std::tuple<std::shared_ptr<xivres::stream>, int, std::map<uint32_t, float>> ResolveStream(const Structs::LookupStruct& lookup);

	// Returns how to make the requested face from the font that ResolveFont returns.
	[[nodiscard]] SynthesizedFace ResolveSynthesis(const Structs::LookupStruct& lookup, Structs::RendererEnum renderer, IDWriteFont* font);

	// Sets Synthesis if unset, changing the requested properties so that the font is drawn as before.
	void ConvertToExplicitSynthesis(Structs::LookupStruct& lookup, Structs::RendererEnum renderer);

	// Merges the wrapped fonts of the elements of a face, but those to skip.
	[[nodiscard]] std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> MergeElements(const Structs::Face& face, const std::set<const Structs::FaceElement*>& skip = {});

	// Drops the fonts read from the game installations.
	void FlushCachedFonts();

	// Sets the function that returns the game installations to read the fonts of a type from, in the order to try them;
	// without one, elements of game fonts are drawn empty.
	void SetGameInstallationPathsProvider(std::function<std::vector<std::filesystem::path>(xivres::font_type fontType)> provider);

	// Sets the function that reports a game installation whose fonts could not be read, once until the cached fonts are
	// flushed; without one, as for runs without a window, nothing is reported. Elements of these fonts are drawn empty.
	void SetGameFontErrorHandler(std::function<void(const std::exception& e)> handler);

	// Folder of the file of the current configuration, which relative paths in it are resolved against; empty if the
	// configuration has not been saved.
	void SetProjectDirectory(std::filesystem::path path);
	[[nodiscard]] std::filesystem::path GetProjectDirectory();

	// Drops the fonts of the elements that read files by paths relative to the project directory.
	void OnProjectDirectoryChange(const Structs::MultiFontSet& multiFontSet);
}
