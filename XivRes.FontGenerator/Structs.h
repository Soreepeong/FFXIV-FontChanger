#pragma once

namespace App::Structs {
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

		xivres::fontgen::image_coverage_mode BitmapCoverage = xivres::fontgen::image_coverage_mode::Auto;

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

	struct EmptyFontDef {
		int Ascent = 0;
		int LineHeight = 0;
	};

	struct SynthesisStruct {
		// Whether weights, styles, and widths that the family lacks are made from the closest face of the family.
		bool Allow = true;
	};

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
		[[nodiscard]] xivres::fontgen::font_render_transformation_matrix GetScreenMatrix() const;

		// Returns the emboldening for FreeType in ems.
		[[nodiscard]] float GetEmbolden() const;
	};

	// Returns the width of the stretch as a percentage of the normal width, as in usWidthClass of OS/2; 0 if undefined.
	[[nodiscard]] float GetStretchPercent(DWRITE_FONT_STRETCH stretch);

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

		std::wstring GetWeightString() const;
		std::wstring GetStretchString() const;
		std::wstring GetStyleString() const;

		// Returns Variations keyed by axis tags in DWRITE_FONT_AXIS_TAG byte order.
		std::map<uint32_t, float> GetVariationAxisValues() const;

		std::pair<IDWriteFactoryPtr, IDWriteFontPtr> ResolveFont() const;

		// Returns the font file, the index of the face in it, and the axis values of the instance if it is a variable font.
		std::tuple<std::shared_ptr<xivres::stream>, int, std::map<uint32_t, float>> ResolveStream() const;

		// Returns how to make the requested face from the font that ResolveFont returns.
		[[nodiscard]] SynthesizedFace ResolveSynthesis(RendererEnum renderer, IDWriteFont* font) const;

		// Sets Synthesis if unset, changing the requested properties so that the font is drawn as before.
		void ConvertToExplicitSynthesis(RendererEnum renderer);
	};

	struct RendererSpecificStruct {
		xivres::fontgen::empty_fixed_size_font::create_struct Empty;
		xivres::fontgen::freetype_fixed_size_font::create_struct FreeType;
		xivres::fontgen::directwrite_fixed_size_font::create_struct DirectWrite;
		GlyphImagesStruct GlyphImages;
	};

	// Scaling, skewing, and rotation of glyphs on screen, where y grows downwards.
	struct TransformStruct {
		using matrix = xivres::fontgen::font_render_transformation_matrix;

		// Negative values mirror.
		float ScaleX = 1.f;
		float ScaleY = 1.f;

		// Positive values make the top of glyphs lean to the right, as italics do.
		float SkewDegrees = 0.f;

		// Positive values rotate counterclockwise.
		float RotationDegrees = 0.f;

		// Matrix stored by earlier versions, passed to renderers as is; DirectWrite and FreeType interpret it differently.
		// When set, the components above are not used.
		std::optional<matrix> LegacyMatrix;

		// Returns the transformation for column vectors on screen: x' = M11 x + M12 y, y' = M21 x + M22 y.
		[[nodiscard]] matrix GetScreenMatrix(RendererEnum renderer) const;

		// Returns the matrix to pass to the renderer.
		[[nodiscard]] matrix GetRendererMatrix(RendererEnum renderer) const;

		[[nodiscard]] static matrix ScreenToRenderer(RendererEnum renderer, const matrix& screen);

		[[nodiscard]] static matrix RendererToScreen(RendererEnum renderer, const matrix& m);

		// Decomposes a transformation on screen into rotation * skew * scale.
		[[nodiscard]] static TransformStruct FromScreenMatrix(const matrix& screen);

		[[nodiscard]] bool IsIdentity() const;
	};

	struct GlyphMergingStruct {
		xivres::fontgen::glyph_merge_params Params;

		// Applied to the texts after the transformation of the element.
		TransformStruct TextTransform;

		// Glyph merging is in use when there is any mapping; the element then only draws the mapped codepoints.
		[[nodiscard]] bool IsEnabled() const { return !Params.Mappings.empty(); }
	};

	class FaceElement {
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> m_baseFont;
		mutable std::shared_ptr<xivres::fontgen::wrapping_fixed_size_font> m_wrappedFont;
		friend struct FontSet;

	public:
		float Size = 0.f;
		float Gamma = 1.f;
		xivres::fontgen::codepoint_merge_mode MergeMode = xivres::fontgen::codepoint_merge_mode::AddNew;
		TransformStruct Transform;
		xivres::fontgen::wrap_modifiers WrapModifiers;
		RendererEnum Renderer = RendererEnum::Empty;
		LookupStruct Lookup;
		RendererSpecificStruct RendererSpecific;
		GlyphMergingStruct GlyphMerging;

		FaceElement() noexcept;
		FaceElement(FaceElement&& r) noexcept;
		FaceElement(const FaceElement& r);
		FaceElement operator=(FaceElement&& r) noexcept;
		FaceElement operator=(const FaceElement& r);

		friend void swap(FaceElement& l, FaceElement& r) noexcept;

		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetBaseFont() const;
		const std::shared_ptr<xivres::fontgen::wrapping_fixed_size_font>& GetWrappedFont() const;

		void FlushCache();

		void OnFontWrappingParametersChange();
		void OnFontCreateParametersChange();

		// Scales the size and the values in pixels, as for a copy of the element in a font of another size.
		void Scale(float factor);

		std::string GetBaseFontKey() const;

		// Whether the font is made from files at a path relative to the project directory.
		[[nodiscard]] bool UsesProjectDirectory() const;

		std::wstring GetRangeRepresentation() const;
		std::wstring GetRendererRepresentation() const;
		std::wstring GetLookupRepresentation() const;
	};

	void swap(FaceElement& l, FaceElement& r) noexcept;

	class Face {
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> MergedFont;
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> PreviewMergedFont;

		// Elements left out of the preview, such as for comparing the face without them; not saved, nor copied.
		mutable std::set<const FaceElement*> DeactivatedElements;

	public:
		std::string Name;
		std::string PreviewText;
		std::vector<std::unique_ptr<FaceElement>> Elements;
		xivres::fontgen::vertical_alignment VerticalAlignment = xivres::fontgen::vertical_alignment::Baseline;

		Face() noexcept;
		Face(Face&& r) noexcept;
		Face(const Face& r);
		Face& operator=(Face&& r) noexcept;
		Face& operator=(const Face& r);

		friend void swap(Face& l, Face& r) noexcept;

		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetMergedFont() const;

		// Returns the merged font without the deactivated elements, for the preview; exports use GetMergedFont.
		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetPreviewMergedFont() const;

		void SetElementDeactivated(const FaceElement& element, bool deactivated);

		void FlushCache();

		void OnElementChange();
	};

	void swap(Face& l, Face& r) noexcept;

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

	void FlushCachedFonts();

	// Whether a missing game installation is reported with a dialog, once until the cached fonts are flushed.
	void SetGameNotFoundDialogsEnabled(bool enabled);

	// Folder of the file of the current configuration, which relative paths in it are resolved against; empty if the
	// configuration has not been saved.
	void SetProjectDirectory(std::filesystem::path path);
	[[nodiscard]] std::filesystem::path GetProjectDirectory();

	// Drops the fonts of the elements that read files by paths relative to the project directory.
	void OnProjectDirectoryChange(const MultiFontSet& multiFontSet);

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

namespace xivres::fontgen {
	void to_json(nlohmann::json& json, const wrap_modifiers& value);

	void from_json(const nlohmann::json& json, wrap_modifiers& value);
}
