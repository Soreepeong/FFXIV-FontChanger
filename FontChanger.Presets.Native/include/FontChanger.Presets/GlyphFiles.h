#pragma once

#include <functional>

#include "Structs.h"

// Glyphs as files: one SVG or PNG file per glyph, named by the codepoint, and a font.json beside them for the metrics
// of the whole font. SVG files are in units of 1/1000 em with y growing downwards and the baseline at y = 880, unless
// their data-xivfont-units-per-em and data-xivfont-baseline attributes say otherwise; the width of the viewBox is the
// advance. PNG files cover the line box at the size in font.json, with the top of the line at the top of the image.
namespace FontChanger::GlyphFiles {
	inline constexpr float DefaultUnitsPerEm = 1000.f;
	inline constexpr float DefaultBaselineY = 880.f;
	inline constexpr auto MetadataFileName = L"font.json";

	// Returns "uniXXXX" for codepoints in the BMP, and "uXXXXX" for the others.
	[[nodiscard]] std::wstring GetGlyphFileStem(char32_t codepoint);

	// Reads the codepoint from a file name such as uni0041.svg, u1F600.png, or U+0041_bold.svg, ignoring the case.
	[[nodiscard]] std::optional<char32_t> ParseGlyphFileName(const std::filesystem::path& path);

	// Glyph files read from a folder or from the embedded files of an element.
	struct GlyphSet {
		std::map<char32_t, xivres::fontgen::image_glyph_source> Glyphs;

		// The PNG files as they are, for embedding.
		std::map<char32_t, std::shared_ptr<const std::string>> PngFiles;

		// Contents of font.json, or null.
		nlohmann::json Metadata;

		// Files that are not taken: of names that are not of codepoints, of codepoints already taken, or unreadable.
		size_t SkippedCount = 0;

		// Why nothing could be read, if so.
		std::wstring Error;
	};

	// Resolves a path relative to the project directory.
	[[nodiscard]] std::filesystem::path ResolvePath(const std::string& path);

	// Reads the glyph files of an element. Folders are read once, until InvalidateFolder is called for them.
	[[nodiscard]] std::shared_ptr<const GlyphSet> LoadGlyphSet(const Structs::GlyphImagesStruct& settings);

	void InvalidateFolder(const std::filesystem::path& folder);

	// Starts watching the folders that fonts are made from: when files in one change, it is invalidated, and the message
	// is posted to the window. Without this, as on the command line, folders are not watched.
	void SetChangeNotificationWindow(HWND hWnd, UINT message);

	// Returns the folders that have changed since the last call.
	[[nodiscard]] std::vector<std::filesystem::path> TakeChangedFolders();

	[[nodiscard]] std::shared_ptr<xivres::fontgen::fixed_size_font> CreateFont(
		const Structs::GlyphImagesStruct& settings,
		float size,
		float gamma,
		const xivres::fontgen::font_render_transformation_matrix& matrix);

	// Stores the files of the folder in the settings, so that the configuration does not need the folder any more.
	void Embed(Structs::GlyphImagesStruct& settings);

	struct ExportOptions {
		bool Svg = true;
		bool Png = true;

		// Whether the font has the adjustments of an element applied; recorded in font.json.
		bool WithAdjustments = false;

		// Describes where the glyphs came from; recorded in font.json.
		nlohmann::json Source;
	};

	// Returns the font of the glyphs that an element adds, with or without its letter spacing, offset, and baseline shift.
	[[nodiscard]] std::shared_ptr<xivres::fontgen::fixed_size_font> GetElementFontForExport(const Structs::FaceElement& element, bool withAdjustments);

	// Describes where the glyphs of an element or a font come from, for the source of ExportOptions.
	[[nodiscard]] nlohmann::json DescribeSource(const Structs::FaceElement& element);
	[[nodiscard]] nlohmann::json DescribeSource(const Structs::Face& face);

	// Writes the glyphs of the font into the folder. progress is called with the number of glyphs written and the
	// total, and may throw to stop.
	void ExportGlyphs(
		const xivres::fontgen::fixed_size_font& font,
		const std::filesystem::path& folder,
		const ExportOptions& options,
		const std::function<void(size_t, size_t)>& progress = {});
}
