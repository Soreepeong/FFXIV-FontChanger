#pragma once

namespace App::Structs {
	enum class RendererEnum : uint8_t {
		Empty,
		PrerenderedGameInstallation,
		DirectWrite,
		FreeType,
	};

	struct EmptyFontDef {
		int Ascent = 0;
		int LineHeight = 0;
	};

	struct LookupStruct {
		std::string Name;
		DWRITE_FONT_WEIGHT Weight = DWRITE_FONT_WEIGHT_REGULAR;
		DWRITE_FONT_STRETCH Stretch = DWRITE_FONT_STRETCH_NORMAL;
		DWRITE_FONT_STYLE Style = DWRITE_FONT_STYLE_NORMAL;
		std::set<DWRITE_FONT_FEATURE_TAG> Features;

		std::wstring GetWeightString() const;
		std::wstring GetStretchString() const;
		std::wstring GetStyleString() const;

		std::pair<IDWriteFactoryPtr, IDWriteFontPtr> ResolveFont() const;
		std::pair<std::shared_ptr<xivres::stream>, int> ResolveStream() const;
	};

	struct RendererSpecificStruct {
		xivres::fontgen::empty_fixed_size_font::create_struct Empty;
		xivres::fontgen::freetype_fixed_size_font::create_struct FreeType;
		xivres::fontgen::directwrite_fixed_size_font::create_struct DirectWrite;
	};

	class FaceElement {
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> m_baseFont;
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> m_wrappedFont;
		friend struct FontSet;

	public:
		float Size = 0.f;
		float Gamma = 1.f;
		xivres::fontgen::codepoint_merge_mode MergeMode = xivres::fontgen::codepoint_merge_mode::AddNew;
		xivres::fontgen::font_render_transformation_matrix TransformationMatrix{1.f, 0.f, 0.f, 1.f};
		xivres::fontgen::wrap_modifiers WrapModifiers;
		RendererEnum Renderer = RendererEnum::Empty;
		LookupStruct Lookup;
		RendererSpecificStruct RendererSpecific;

		FaceElement() noexcept;
		FaceElement(FaceElement&& r) noexcept;
		FaceElement(const FaceElement& r);
		FaceElement operator=(FaceElement&& r) noexcept;
		FaceElement operator=(const FaceElement& r);

		friend void swap(FaceElement& l, FaceElement& r) noexcept;

		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetBaseFont() const;
		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetWrappedFont() const;

		void OnFontWrappingParametersChange();
		void OnFontCreateParametersChange();

		std::string GetBaseFontKey() const;

		std::wstring GetRangeRepresentation() const;
		std::wstring GetRendererRepresentation() const;
		std::wstring GetLookupRepresentation() const;
	};

	void swap(FaceElement& l, FaceElement& r) noexcept;

	class Face {
		mutable std::shared_ptr<xivres::fontgen::fixed_size_font> MergedFont;

	public:
		std::string Name;
		std::string PreviewText;
		std::vector<std::unique_ptr<FaceElement>> Elements;

		Face() noexcept;
		Face(Face&& r) noexcept;
		Face(const Face& r);
		Face& operator=(Face&& r) noexcept;
		Face& operator=(const Face& r);

		friend void swap(Face& l, Face& r) noexcept;

		const std::shared_ptr<xivres::fontgen::fixed_size_font>& GetMergedFont() const;

		void OnElementChange();
	};

	void swap(Face& l, Face& r) noexcept;

	struct FontSet {
		std::string TexFilenameFormat;
		int DiscardStep = 1;
		int SideLength = 4096;
		int ExpectedTexCount = 1;
		std::vector<std::unique_ptr<Face>> Faces;

		void ConsolidateFonts() const;

		static FontSet NewFromTemplateFont(xivres::font_type fontType);
	};

	struct MultiFontSet {
		std::vector<std::unique_ptr<FontSet>> FontSets;
		bool ExportMapFontLobbyToFont = true;
		bool ExportMapChnAxisToFont = true;
		bool ExportMapKrnAxisToFont = true;
		bool ExportMapTcAxisToFont = true;
	};

	void to_json(nlohmann::json& json, const LookupStruct& value);

	void from_json(const nlohmann::json& json, LookupStruct& value);

	void to_json(nlohmann::json& json, const RendererSpecificStruct& value);

	void from_json(const nlohmann::json& json, RendererSpecificStruct& value);

	void to_json(nlohmann::json& json, const FaceElement& value);

	void from_json(const nlohmann::json& json, FaceElement& value);

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
