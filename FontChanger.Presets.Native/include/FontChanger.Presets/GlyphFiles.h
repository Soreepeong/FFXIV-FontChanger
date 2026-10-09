#pragma once

#include <functional>

#include "FontChanger.FixedSizeFont/image_glyph_files.h"

#include "Structs.h"

// The glyph files of elements (FontChanger.FixedSizeFont/image_glyph_files.h): read from their folder, relative to the project
// directory, or from the files embedded in the element.
namespace FontChanger::GlyphFiles {
	// Resolves a path relative to the project directory.
	[[nodiscard]] std::filesystem::path ResolvePath(const std::string& path);

	// Reads the glyph files of an element. Folders are read once, until InvalidateFolder is called for them.
	[[nodiscard]] std::shared_ptr<const FontChanger::FixedSizeFont::glyph_file_set> LoadGlyphSet(const Structs::GlyphImagesStruct& settings);

	void InvalidateFolder(const std::filesystem::path& folder);

	// Sets the function that is told of each folder that fonts are made from, such as to watch them for changes.
	void SetFolderObserver(std::function<void(const std::filesystem::path& folder)> observer);

	[[nodiscard]] std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> CreateFont(
		const Structs::GlyphImagesStruct& settings,
		float size,
		float gamma,
		const FontChanger::FixedSizeFont::font_render_transformation_matrix& matrix);
}
