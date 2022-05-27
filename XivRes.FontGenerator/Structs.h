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

		std::wstring GetWeightString() const;
		std::wstring GetStretchString() const;
		std::wstring GetStyleString() const;

		std::pair<IDWriteFactoryPtr, IDWriteFontPtr> ResolveFont() const;
		std::pair<std::shared_ptr<XivRes::IStream>, int> ResolveStream() const;
	};

	struct RendererSpecificStruct {
		XivRes::FontGenerator::EmptyFixedSizeFont::CreateStruct Empty;
		XivRes::FontGenerator::FreeTypeFixedSizeFont::CreateStruct FreeType;
		XivRes::FontGenerator::DirectWriteFixedSizeFont::CreateStruct DirectWrite;
	};

	class FaceElement {
		mutable std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont> m_baseFont;
		mutable std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont> m_wrappedFont;
		friend struct FontSet;

	public:
		float Size = 0.f;
		float Gamma = 1.f;
		XivRes::FontGenerator::MergedFontCodepointMode MergeMode = XivRes::FontGenerator::MergedFontCodepointMode::AddNew;
		XivRes::FontGenerator::FontRenderTransformationMatrix TransformationMatrix{ 1.f, 0.f, 0.f, 1.f };
		XivRes::FontGenerator::WrapModifiers WrapModifiers;
		RendererEnum Renderer = RendererEnum::Empty;
		LookupStruct Lookup;
		RendererSpecificStruct RendererSpecific;

		FaceElement() noexcept;
		FaceElement(FaceElement&& r) noexcept;
		FaceElement(const FaceElement& r);
		FaceElement operator=(FaceElement&& r) noexcept;
		FaceElement operator=(const FaceElement& r);

		friend void swap(FaceElement& l, FaceElement& r) noexcept;

		const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& GetBaseFont() const;
		const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& GetWrappedFont() const;

		void OnFontWrappingParametersChange();
		void OnFontCreateParametersChange();

		std::string GetBaseFontKey() const;

		std::wstring GetRangeRepresentation() const;
		std::wstring GetRendererRepresentation() const;
		std::wstring GetLookupRepresentation() const;
	};

	void swap(FaceElement& l, FaceElement& r) noexcept;

	class Face {
		mutable std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont> MergedFont;

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

		const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& GetMergedFont() const;

		void OnElementChange();
	};

	void swap(Face& l, Face& r) noexcept;

	struct FontSet {
		std::string TexFilenameFormat;
		int DiscardStep = 1;
		int SideLength = 4096;
		std::vector<std::unique_ptr<Face>> Faces;

		void ConsolidateFonts();

		static FontSet NewFromTemplateFont(XivRes::GameFontType fontType);
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

	const char* GetDefaultPreviewText();
}

namespace XivRes::FontGenerator {
	void to_json(nlohmann::json& json, const WrapModifiers& value);

	void from_json(const nlohmann::json& json, WrapModifiers& value);
}
