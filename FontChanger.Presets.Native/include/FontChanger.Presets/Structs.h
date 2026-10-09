#pragma once

#include <exception>
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <tuple>
#include <vector>

#include <nlohmann/json.hpp>

#include "xivres/fontdata.h"
#include "FontChanger.FixedSizeFont/directwrite_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/fontdata_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/freetype_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/glyph_merging_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/image_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/merged_fixed_size_font.h"
#include "FontChanger.FixedSizeFont/wrapping_fixed_size_font.h"

// The preset model and its JSON: the C++ twin of FontChanger.Presets/Preset.cs. The fonts that elements and faces make
// are made by ElementFonts.cpp (ElementFonts.h).
namespace FontChanger::Structs {
	enum class RendererEnum : uint8_t {
		Empty,
		PrerenderedGameInstallation,
		DirectWrite,
		FreeType,

		// Glyphs from SVG and PNG files, one per glyph; see GlyphFiles.h.
		GlyphImages,
	};

	struct GlyphImagesStruct {
		// Folder of the files, absolute or relative to the project directory.
		std::string Path;

		// Units of SVG files and PNG files, which override their attributes and font.json when set.
		std::optional<float> UnitsPerEm;
		std::optional<float> BaselineY;

		// Line metrics in units of UnitsPerEm, which override font.json when set.
		std::optional<float> Ascent;
		std::optional<float> LineHeight;

		FontChanger::FixedSizeFont::image_coverage_mode BitmapCoverage = FontChanger::FixedSizeFont::image_coverage_mode::Auto;

		// Files stored in the configuration instead of the folder: font.json, and the files of the glyphs, which are SVG
		// documents, or PNG files as base64.
		struct EmbeddedGlyph {
			std::shared_ptr<const std::string> Svg;
			std::shared_ptr<const std::string> PngBase64;
		};
		std::optional<nlohmann::json> EmbeddedMetadata;
		std::map<char32_t, EmbeddedGlyph> Embedded;

		[[nodiscard]] bool IsEmbedded() const { return !Embedded.empty(); }
	};

	struct SynthesisStruct {
		// Whether weights, styles, and widths that the family lacks are made from the closest face of the family.
		bool Allow = true;
	};

	struct LookupStruct {
		std::string Name;
		DWRITE_FONT_WEIGHT Weight = DWRITE_FONT_WEIGHT_REGULAR;
		DWRITE_FONT_STRETCH Stretch = DWRITE_FONT_STRETCH_NORMAL;
		DWRITE_FONT_STYLE Style = DWRITE_FONT_STYLE_NORMAL;

		// Unset for files of earlier versions: DirectWrite then keeps the simulations that it picks while matching the
		// font, and FreeType draws the matched font as is. When set, ResolveFont returns a real face, and the requested
		// properties that it lacks are synthesized as ResolveSynthesis says.
		std::optional<SynthesisStruct> Synthesis;

		// Enabled OpenType features and their values; values above 1 pick an alternate for features such as 'salt' and 'cv01'.
		std::map<DWRITE_FONT_FEATURE_TAG, uint32_t> Features;
		std::string Language;

		// Axis values of variable fonts, such as {"wght", 450}, that override the instance of the font.
		std::map<std::string, float> Variations;

		// Returns Variations keyed by axis tags in DWRITE_FONT_AXIS_TAG byte order.
		std::map<uint32_t, float> GetVariationAxisValues() const;
	};

	struct RendererSpecificStruct {
		FontChanger::FixedSizeFont::empty_fixed_size_font::create_struct Empty;
		FontChanger::FixedSizeFont::freetype_fixed_size_font::create_struct FreeType;
		FontChanger::FixedSizeFont::directwrite_fixed_size_font::create_struct DirectWrite;
		GlyphImagesStruct GlyphImages;
	};

	// Scaling, skewing, and rotation of glyphs on screen, where y grows downwards.
	struct TransformStruct {
		using matrix = FontChanger::FixedSizeFont::font_render_transformation_matrix;

		// Negative values mirror.
		float ScaleX = 1.f;
		float ScaleY = 1.f;

		// Positive values make the top of glyphs lean to the right, as italics do.
		float SkewDegrees = 0.f;

		// Positive values rotate counterclockwise.
		float RotationDegrees = 0.f;

		// Returns the transformation for column vectors on screen: x' = M11 x + M12 y, y' = M21 x + M22 y.
		[[nodiscard]] matrix GetScreenMatrix() const;

		// Returns a * b, with matrices laid out as [[M11, M12], [M21, M22]].
		[[nodiscard]] static matrix Multiply(const matrix& a, const matrix& b);

		// Returns the matrix of a transformation on screen as the renderer takes it, and the other way around: each
		// conversion is its own inverse.
		[[nodiscard]] static matrix ScreenToRenderer(RendererEnum renderer, const matrix& screen);

		// Decomposes a transformation on screen into rotation * skew * scale.
		[[nodiscard]] static TransformStruct FromScreenMatrix(const matrix& screen);
	};

	struct GlyphMergingStruct {
		FontChanger::FixedSizeFont::glyph_merge_params Params;

		// Applied to the texts after the transformation of the element.
		TransformStruct TextTransform;

		// Glyph merging is in use when there is any mapping; the element then only draws the mapped codepoints.
		[[nodiscard]] bool IsEnabled() const { return !Params.Mappings.empty(); }
	};

	class FaceElement {
		mutable std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> m_baseFont;
		mutable std::shared_ptr<FontChanger::FixedSizeFont::wrapping_fixed_size_font> m_wrappedFont;
		friend struct FontSet;

	public:
		float Size = 0.f;
		float Gamma = 1.f;
		FontChanger::FixedSizeFont::codepoint_merge_mode MergeMode = FontChanger::FixedSizeFont::codepoint_merge_mode::AddNew;
		TransformStruct Transform;
		FontChanger::FixedSizeFont::wrap_modifiers WrapModifiers;
		RendererEnum Renderer = RendererEnum::Empty;
		LookupStruct Lookup;
		RendererSpecificStruct RendererSpecific;
		GlyphMergingStruct GlyphMerging;

		// The fonts of the element, made by ElementFonts.cpp when first asked for, until the parameters change.
		const std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>& GetBaseFont() const;
		const std::shared_ptr<FontChanger::FixedSizeFont::wrapping_fixed_size_font>& GetWrappedFont() const;

		// Drops the wrapped font, for changes of how it picks and moves the glyphs of the base font.
		void OnFontWrappingParametersChange();

		// Drops the fonts, for changes of what the base font is made from.
		void OnFontCreateParametersChange();

		// Scales the size and the values in pixels, as for a copy of the element in a font of another size.
		void Scale(float factor);

		// Returns what the base font is made from, which elements of the same base font share.
		std::string GetBaseFontKey() const;

		// Whether the font is made from files at a path relative to the project directory.
		[[nodiscard]] bool UsesProjectDirectory() const;
	};

	class Face {
		mutable std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> MergedFont;

	public:
		std::string Name;
		std::string PreviewText;
		std::vector<std::unique_ptr<FaceElement>> Elements;
		FontChanger::FixedSizeFont::vertical_alignment VerticalAlignment = FontChanger::FixedSizeFont::vertical_alignment::Baseline;

		Face() noexcept = default;
		Face(Face&& r) noexcept = default;
		Face(const Face& r);
		Face& operator=(Face&& r) noexcept = default;
		Face& operator=(const Face& r);

		// The merged font of the elements, made by ElementFonts.cpp when first asked for, until the elements change.
		const std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>& GetMergedFont() const;

		void FlushCache();

		void OnElementChange();
	};

	struct FontSet {
		std::string TexFilenameFormat;
		int DiscardStep = 1;
		int SideLength = 4096;
		int ExpectedTexCount = 1;
		std::vector<std::unique_ptr<Face>> Faces;

		void FlushCache();

		void ConsolidateFonts() const;

		static FontSet NewFromTemplateFont(xivres::font_type fontType);
	};

	struct MultiFontSet {
		std::vector<std::unique_ptr<FontSet>> FontSets;
		bool ExportMapFontLobbyToFont = true;
		bool ExportMapChnAxisToFont = true;
		bool ExportMapKrnAxisToFont = true;
		bool ExportMapTcAxisToFont = true;

		void FlushCache();
	};

	void to_json(nlohmann::json& json, const LookupStruct& value);

	void from_json(const nlohmann::json& json, LookupStruct& value);

	void to_json(nlohmann::json& json, const RendererSpecificStruct& value);

	void from_json(const nlohmann::json& json, RendererSpecificStruct& value);

	void to_json(nlohmann::json& json, const FaceElement& value);

	void from_json(const nlohmann::json& json, FaceElement& value);

	void to_json(nlohmann::json& json, const TransformStruct& value);

	void from_json(const nlohmann::json& json, TransformStruct& value);

	void to_json(nlohmann::json& json, const GlyphMergingStruct& value);

	void from_json(const nlohmann::json& json, GlyphMergingStruct& value);

	void to_json(nlohmann::json& json, const Face& value);

	void from_json(const nlohmann::json& json, Face& value);

	void to_json(nlohmann::json& json, const FontSet& value);

	void from_json(const nlohmann::json& json, FontSet& value);

	void to_json(nlohmann::json& json, const MultiFontSet& value);

	void from_json(const nlohmann::json& json, MultiFontSet& value);

	const char* GetDefaultPreviewText();
}

namespace FontChanger::FixedSizeFont {
	void to_json(nlohmann::json& json, const wrap_modifiers& value);

	void from_json(const nlohmann::json& json, wrap_modifiers& value);
}
