#include "pch.h"
#include "FaceFromFont.h"

#include "FaceElementEditorDialog.Internal.h"

using App::Structs::FaceElement;
using App::Structs::LookupStruct;
using App::Structs::RendererEnum;
using xivres::fontgen::codepoint_merge_mode;
using xivres::fontgen::glyph_merge_mapping;
using xivres::fontgen::glyph_merge_shape;

namespace {
	using CodepointRanges = std::vector<std::pair<char32_t, char32_t>>;

	const CodepointRanges AllCodepoints{{0x20, 0x10FFFF}};
	const CodepointRanges LatinCodepoints{{0x20, 0x2FFF}};
	const CodepointRanges CjkCodepoints{{0x3000, 0x10FFFF}};
	const CodepointRanges HangulCodepoints{{0x1100, 0x11FF}, {0x3130, 0x318F}, {0xAC00, 0xD7AF}};
	const CodepointRanges HanCodepoints{{0x3400, 0x4DBF}, {0x4E00, 0x9FFF}};
	const CodepointRanges PrivateUseCodepoints{{0xE000, 0xF8FF}};

	// The game's PUA glyphs, as the Lodestone web font (FFXIV_Lodestone_SSF) draws them.
	constexpr auto LodestoneFontName = "XIV AXIS Std ATK";

	// Fonts of every Windows installation that draw the texts of merged glyphs in scripts that the chosen font lacks.
	constexpr std::array MergedTextFallbackFontNames{"Yu Gothic UI", "Malgun Gothic"};

	struct FamilyTraits {
		// Glyph whose height is matched: the capital H, or 8 for the families of only digits (JupiterN's are old style,
		// where 8 is as tall as in lining figures).
		char32_t ReferenceGlyph;

		// Height of the reference glyph of the game's font over its size, measured from the game's glyphs; that of AXIS is
		// of AXIS Basic ProN, which draws AXIS as the game does.
		float ReferenceHeight;

		// Whether the family has text, and not only digits.
		bool HasText;

		// Whether the family is AXIS, of any client, whose faces have the game's symbols and IME indicators.
		bool IsAxis;

		// OpenType features of the look of the family: Jupiter's small capitals and old style figures.
		std::vector<DWRITE_FONT_FEATURE_TAG> Features;
	};

	FamilyTraits GetFamilyTraits(std::string_view family) {
		if (family == "Jupiter")
			return {U'H', 0.773f, true, false, {DWRITE_FONT_FEATURE_TAG_OLD_STYLE_FIGURES, DWRITE_FONT_FEATURE_TAG_SMALL_CAPITALS}};
		if (family == "JupiterN")
			return {U'8', 0.76f, false, false, {DWRITE_FONT_FEATURE_TAG_OLD_STYLE_FIGURES}};
		if (family == "Meidinger")
			return {U'8', 0.71f, false, false, {}};
		if (family == "MiedingerMid")
			return {U'H', 0.705f, true, false, {}};
		if (family == "TrumpGothic")
			return {U'H', 0.761f, true, false, {}};
		return {U'H', 0.768f, true, true, {}};
	}

	// Pixels that the game's AXIS draws Latin glyphs to the right of where AXIS Basic ProN would be at its sizes, and
	// assumed to grow alike with the size for the others.
	float GetLatinOffset(const FamilyTraits& traits, float size) {
		if (traits.IsAxis) {
			for (const auto& [axisSize, offset] : std::initializer_list<std::pair<float, float>>{{9.6f, 1.f}, {12.f, 1.f}, {14.f, 1.f}, {18.f, 1.f}, {36.f, 3.f}}) {
				if (std::abs(size - axisSize) < 0.05f)
					return offset;
			}
		}
		return (std::max)(1.f, std::round(size / 12));
	}

	// Pixels that the game's AXIS draws CJK and PUA glyphs to the right and down from where they would be; at 36 px, they
	// are a pixel right and up.
	std::pair<float, float> GetCjkPlacement(const FamilyTraits& traits, float size) {
		if (traits.IsAxis && std::abs(size - 36) < 0.05f)
			return {1.f, -1.f};
		return {0.f, 0.f};
	}

	class FontInfo {
		IDWriteFontPtr m_font;
		IDWriteFontFacePtr m_face;
		DWRITE_FONT_METRICS m_metrics{};
		std::set<int> m_bitmapSizes;

	public:
		explicit FontInfo(const LookupStruct& lookup) {
			m_font = lookup.ResolveFont().second;
			SuccessOrThrow(m_font->CreateFontFace(&m_face));
			m_face->GetMetrics(&m_metrics);

			// Sizes of bitmaps (ppemY of the BitmapSize records of 48 bytes after the header of EBLC or CBLC).
			for (const auto tag : {DWRITE_MAKE_OPENTYPE_TAG('E', 'B', 'L', 'C'), DWRITE_MAKE_OPENTYPE_TAG('C', 'B', 'L', 'C')}) {
				const void* data;
				UINT32 size;
				void* context;
				BOOL exists;
				if (FAILED(m_face->TryGetFontTable(tag, &data, &size, &context, &exists)) || !exists)
					continue;

				const auto p = static_cast<const uint8_t*>(data);
				if (size >= 8) {
					const auto count = static_cast<uint32_t>(p[4]) << 24 | static_cast<uint32_t>(p[5]) << 16 | static_cast<uint32_t>(p[6]) << 8 | p[7];
					for (uint32_t i = 0; i < count && 8 + 48 * (i + 1) <= size; i++)
						m_bitmapSizes.insert(p[8 + 48 * i + 45]);
				}
				m_face->ReleaseFontTable(context);
			}
		}

		// Returns whether the font has the characters of a text, other than the line breaks and the bar of IME indicators.
		[[nodiscard]] bool Has(std::u32string_view text) const {
			if (text.starts_with(U'_'))
				text = text.substr(1);
			for (const auto c : text) {
				BOOL exists = FALSE;
				if (c != U'\n' && (FAILED(m_font->HasCharacter(c, &exists)) || !exists))
					return false;
			}
			return true;
		}

		// Returns the height of the ink of a glyph over the em, from the baseline (or its bottom, if above it); 0 if none.
		[[nodiscard]] float GetInkHeight(char32_t c) const {
			const auto codepoint = static_cast<UINT32>(c);
			UINT16 glyph = 0;
			if (FAILED(m_face->GetGlyphIndices(&codepoint, 1, &glyph)) || !glyph)
				return 0;

			DWRITE_GLYPH_METRICS gm;
			if (FAILED(m_face->GetDesignGlyphMetrics(&glyph, 1, &gm, FALSE)))
				return 0;

			const auto top = gm.verticalOriginY - gm.topSideBearing;
			const auto bottom = top - (static_cast<int>(gm.advanceHeight) - gm.topSideBearing - gm.bottomSideBearing);
			return static_cast<float>(top - (std::max)(0, bottom)) / m_metrics.designUnitsPerEm;
		}

		[[nodiscard]] bool HasBitmapOf(float size) const {
			return m_bitmapSizes.contains(static_cast<int>(size));
		}

		// Returns a glyph's left and right side bearings and its advance over the em; zeros if the font lacks it.
		[[nodiscard]] std::array<float, 3> GetHorizontalMetrics(char32_t c) const {
			const auto codepoint = static_cast<UINT32>(c);
			UINT16 glyph = 0;
			DWRITE_GLYPH_METRICS gm;
			if (FAILED(m_face->GetGlyphIndices(&codepoint, 1, &glyph)) || !glyph || FAILED(m_face->GetDesignGlyphMetrics(&glyph, 1, &gm, FALSE)))
				return {};
			const auto em = static_cast<float>(m_metrics.designUnitsPerEm);
			return {gm.leftSideBearing / em, gm.rightSideBearing / em, gm.advanceWidth / em};
		}
	};

	std::optional<FontInfo> TryOpen(const LookupStruct& lookup) {
		try {
			return FontInfo(lookup);
		} catch (...) {
			return std::nullopt;
		}
	}

	// Returns the size to draw a font at for its glyph to be as tall as the given height, and how to draw it there:
	// natural at sizes of the font's bitmaps (rounded to them), natural symmetric otherwise.
	std::pair<float, DWRITE_RENDERING_MODE> GetSizeAndMode(const FontInfo& font, float exactSize) {
		if (const auto rounded = std::round(exactSize); rounded > 0 && font.HasBitmapOf(rounded))
			return {rounded, DWRITE_RENDERING_MODE_NATURAL};
		return {std::round(exactSize * 10) / 10, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC};
	}

	struct Placement {
		float HorizontalOffset = 0;
		float BaselineShift = 0;
		float LetterSpacing = 0;
	};

	// JupiterN's exclamation mark with Source Han Sans K, as tuned in the game: the gap between the digits' ink and its own
	// over the size, and the distance between the marks of "!!" over the width of their ink.
	constexpr auto ExclamationGapPerSize = -0.256f;
	constexpr auto ExclamationPitchPerInk = 0.424f;

	struct ExclamationMarkPlacement {
		float DigitOffset;
		float Size;
		DWRITE_RENDERING_MODE RenderMode;
		Placement Mark;
	};

	// Places JupiterN's exclamation mark of critical hits by the font's glyphs: the gap between the ink of the digit before
	// it (the one reaching furthest right) and its own, and the distance between the marks of "!!" over the ink's width, as
	// in proportion they are with Source Han Sans K. A gap too wide moves the digits right (within a third of an em) rather
	// than the mark left, as glyphs may not start left of their pens; too narrow, the mark right.
	ExclamationMarkPlacement PlaceExclamationMark(const FontInfo& digits, const FontInfo& mark, float fontSize, float size, float latinOffset) {
		const auto [markSize, renderMode] = GetSizeAndMode(mark, fontSize * 146 / 96);
		auto markOffset = std::round(-8.f / 96 * size);

		auto digitRight = std::numeric_limits<float>::max();
		for (auto c = U'0'; c <= U'9'; c++) {
			if (const auto [left, right, advance] = digits.GetHorizontalMetrics(c); advance > 0)
				digitRight = (std::min)(digitRight, right);
		}
		digitRight = digitRight == std::numeric_limits<float>::max() ? 0 : digitRight * fontSize;
		const auto [markLeft, markRight, markAdvance] = mark.GetHorizontalMetrics(U'!');

		// Of the gap with the digits where Latin glyphs are, too much moves the digits right, and too little the mark.
		const auto excess = markOffset + markLeft * markSize + digitRight - latinOffset - ExclamationGapPerSize * size;
		const auto digitOffset = latinOffset + std::round(std::clamp(excess, 0.f, size / 3));
		if (excess < 0)
			markOffset -= std::round(excess);

		// The advance between marks.
		const auto inkWidth = (markAdvance - markLeft - markRight) * markSize;
		return {digitOffset, markSize, renderMode, {
			.HorizontalOffset = markOffset,
			.BaselineShift = std::round(15.f / 96 * size),
			.LetterSpacing = std::round(ExclamationPitchPerInk * inkWidth - markAdvance * markSize),
		}};
	}

	std::unique_ptr<FaceElement> MakeElement(LookupStruct lookup, float size, DWRITE_RENDERING_MODE renderMode, float gamma, CodepointRanges codepoints, codepoint_merge_mode mergeMode, const Placement& placement = {}) {
		if (!lookup.Synthesis)
			lookup.Synthesis = App::Structs::SynthesisStruct{};

		auto element = std::make_unique<FaceElement>();
		element->Size = size;
		element->Gamma = gamma;
		element->MergeMode = mergeMode;
		element->Renderer = RendererEnum::DirectWrite;
		element->Lookup = std::move(lookup);
		element->RendererSpecific.DirectWrite.RenderMode = renderMode;
		element->RendererSpecific.DirectWrite.MeasureMode = DWRITE_MEASURING_MODE_GDI_NATURAL;
		element->RendererSpecific.DirectWrite.GridFitMode = DWRITE_GRID_FIT_MODE_ENABLED;
		element->WrapModifiers.Codepoints = std::move(codepoints);
		element->WrapModifiers.HorizontalOffset = placement.HorizontalOffset;
		element->WrapModifiers.BaselineShift = placement.BaselineShift;
		element->WrapModifiers.LetterSpacing = placement.LetterSpacing;
		return element;
	}

	CodepointRanges ToRanges(const std::set<char32_t>& codepoints) {
		CodepointRanges res;
		for (const auto c : codepoints) {
			if (!res.empty() && res.back().second + 1 == c)
				res.back().second = c;
			else
				res.emplace_back(c, c);
		}
		return res;
	}

	// The IME indicators, drawn as the game's at each size: the full-width ones in a box, the half-width ones with a bar
	// cut out at the lower left (texts beginning with "_").
	glyph_merge_mapping MakeImeIndicators() {
		glyph_merge_mapping mapping{.Shape = glyph_merge_shape::Ime};
		for (const auto& [c, text] : std::initializer_list<std::pair<char32_t, const char32_t*>>{
			{0xE020, U"あ"},
			{0xE021, U"ア"},
			{0xE022, U"A"},
			{0xE023, U"_ｱ"},
			{0xE024, U"_A"},
			{0xE025, U"가"},
			{0xE026, U"中"},
			{0xE027, U"英"},
		}) {
			mapping.Codepoints.push_back(c);
			mapping.Texts.emplace_back(text);
		}
		return mapping;
	}

	// Makes the glyph merging elements of a face of AXIS: the game's symbols and the IME indicators drawn in the font,
	// and those whose texts it lacks in the first of Windows' fonts that has them. Mappings whose texts no font has are
	// left out, for the glyphs of the game or of the Lodestone font to stay.
	std::vector<std::unique_ptr<FaceElement>> MakeMergedGlyphElements(const LookupStruct& lookup, const FontInfo& font, float size) {
		std::vector<glyph_merge_mapping> mappings;
		const auto& presets = App::FaceElementEditorDialogInternal::GetGlyphMergingPresets();
		for (size_t i = 0; i < presets.size(); i++) {
			if (i != App::FaceElementEditorDialogInternal::GlyphMergingPresetImeIndicators)
				mappings.insert(mappings.end(), presets[i].Mappings.begin(), presets[i].Mappings.end());
		}
		mappings.push_back(MakeImeIndicators());

		std::vector<std::pair<LookupStruct, const FontInfo*>> fonts{{lookup, &font}};
		std::vector<std::optional<FontInfo>> fallbackInfos;
		fallbackInfos.reserve(MergedTextFallbackFontNames.size());
		for (const auto name : MergedTextFallbackFontNames) {
			LookupStruct fallback{.Name = name, .Synthesis = App::Structs::SynthesisStruct{}};
			if (auto& info = fallbackInfos.emplace_back(TryOpen(fallback)))
				fonts.emplace_back(std::move(fallback), &*info);
		}

		// Each mapping's codepoints go to the first font that has their texts.
		std::vector<std::vector<glyph_merge_mapping>> mappingsByFont(fonts.size());
		for (const auto& mapping : mappings) {
			std::vector<glyph_merge_mapping> parts(fonts.size());
			for (size_t i = 0; i < mapping.Codepoints.size(); i++) {
				const auto& text = i < mapping.Texts.size() ? mapping.Texts[i] : std::u32string();
				for (size_t f = 0; f < fonts.size(); f++) {
					if (!fonts[f].second->Has(text))
						continue;

					auto& part = parts[f];
					if (part.Codepoints.empty()) {
						part = mapping;
						part.Codepoints.clear();
						part.Texts.clear();
					}
					part.Codepoints.push_back(mapping.Codepoints[i]);
					part.Texts.push_back(text);
					break;
				}
			}
			for (size_t f = 0; f < fonts.size(); f++) {
				if (!parts[f].Codepoints.empty())
					mappingsByFont[f].push_back(std::move(parts[f]));
			}
		}

		std::vector<std::unique_ptr<FaceElement>> res;
		for (size_t f = 0; f < fonts.size(); f++) {
			if (mappingsByFont[f].empty())
				continue;

			std::set<char32_t> codepoints;
			for (const auto& mapping : mappingsByFont[f])
				codepoints.insert(mapping.Codepoints.begin(), mapping.Codepoints.end());

			// Replacing the glyphs that the game's font has, at the size of the game's own, as the shapes are made for.
			auto element = MakeElement(fonts[f].first, size, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC, 1.4f, ToRanges(codepoints), codepoint_merge_mode::Replace);
			element->GlyphMerging.Params.Mappings = std::move(mappingsByFont[f]);
			res.emplace_back(std::move(element));
		}
		return res;
	}
}

std::optional<std::pair<std::string_view, float>> App::FaceFromFont::GetGameFontFamilyAndSize(std::string_view faceName) {
	for (const auto fontType : {xivres::font_type::font, xivres::font_type::font_lobby, xivres::font_type::chn_axis, xivres::font_type::krn_axis, xivres::font_type::tc_axis}) {
		for (const auto& def : xivres::fontgen::get_fontdata_definition(fontType)) {
			std::string_view filename(def.Path);
			filename = filename.substr(filename.rfind('/') + 1);
			filename = filename.substr(0, filename.find('.'));
			if (filename == faceName)
				return std::make_pair(std::string_view(def.Name), def.Size);
		}
	}
	return std::nullopt;
}

std::optional<float> App::FaceFromFont::GetGameFontSize(const Structs::Face& face) {
	for (const auto& element : face.Elements) {
		if (element->Renderer == RendererEnum::PrerenderedGameInstallation)
			return element->Size;
	}
	if (const auto familyAndSize = GetGameFontFamilyAndSize(face.Name))
		return familyAndSize->second;
	return std::nullopt;
}

std::vector<std::unique_ptr<FaceElement>> App::FaceFromFont::MakeElements(const LookupStruct& chosen, std::string_view family, float size) {
	const auto traits = GetFamilyTraits(family);
	const FontInfo font(chosen);

	auto lookup = chosen;
	lookup.Features[DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES] = 1;
	for (const auto feature : traits.Features)
		lookup.Features[feature] = 1;

	auto inkHeight = font.GetInkHeight(traits.ReferenceGlyph);
	if (inkHeight <= 0)
		inkHeight = traits.ReferenceHeight;
	const auto [fontSize, renderMode] = GetSizeAndMode(font, size * traits.ReferenceHeight / inkHeight);
	const auto [cjkOffset, cjkShift] = GetCjkPlacement(traits, size);
	// JupiterN's exclamation mark, italic, measured as DirectWrite simulates it if the family has no italic; and the digits
	// moved toward it.
	auto italic = chosen;
	italic.Style = DWRITE_FONT_STYLE_ITALIC;
	std::optional<ExclamationMarkPlacement> mark;
	if (family == "JupiterN") {
		auto measured = italic;
		measured.Synthesis.reset();
		const auto italicFont = TryOpen(measured);
		mark = PlaceExclamationMark(font, italicFont ? *italicFont : font, fontSize, size, GetLatinOffset(traits, size));
	}
	const Placement latin{.HorizontalOffset = mark ? mark->DigitOffset : GetLatinOffset(traits, size)};
	const Placement cjk{.HorizontalOffset = cjkOffset, .BaselineShift = cjkShift};

	const auto hasKana = font.Has(U"あア");
	const auto hasHangul = font.Has(U"가");
	const auto hasHan = font.Has(U"中");

	// Fonts made for Chinese, which have simplified characters, add the Han characters that the game's fonts lack.
	const auto isChinese = font.Has(U"们");

	std::vector<std::unique_ptr<FaceElement>> res;
	if (traits.HasText && hasHangul)
		res.emplace_back(MakeElement(lookup, fontSize, renderMode, 1.2f, HangulCodepoints, codepoint_merge_mode::AddAll, cjk));
	if (traits.HasText && isChinese)
		res.emplace_back(MakeElement(lookup, fontSize, renderMode, 1.2f, HanCodepoints, codepoint_merge_mode::AddAll, cjk));

	// The glyphs that the game's font has; in AXIS, CJK characters are placed apart from Latin ones, as the game does.
	if (traits.IsAxis && (hasKana || hasHangul || hasHan)) {
		res.emplace_back(MakeElement(lookup, fontSize, renderMode, 1.2f, LatinCodepoints, codepoint_merge_mode::Replace, latin));
		res.emplace_back(MakeElement(lookup, fontSize, renderMode, 1.2f, CjkCodepoints, codepoint_merge_mode::Replace, cjk));
	} else {
		res.emplace_back(MakeElement(lookup, fontSize, renderMode, 1.2f, AllCodepoints, codepoint_merge_mode::Replace, latin));
	}

	// JupiterN's exclamation mark of critical hits: italic, half again as large as the digits, down past the baseline,
	// leaning over the digit before it, and the digit after it close.
	if (mark)
		res.emplace_back(MakeElement(italic, mark->Size, mark->RenderMode, 1.2f, {{U'!', U'!'}}, codepoint_merge_mode::Replace, mark->Mark));

	if (traits.IsAxis) {
		// The game's PUA glyphs as vectors, so that they scale alike at sizes that the game has no font of; after the
		// font's elements, which decide how the face is measured.
		res.emplace_back(MakeElement({.Name = LodestoneFontName}, size, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC, 1.4f, PrivateUseCodepoints, codepoint_merge_mode::Replace, {
			.HorizontalOffset = std::abs(size - 36) < 0.05f ? 3.f : 1.f,
			.BaselineShift = cjkShift,
		}));

		auto merged = MakeMergedGlyphElements(chosen, font, size);
		res.insert(res.end(), std::make_move_iterator(merged.begin()), std::make_move_iterator(merged.end()));
	}

	return res;
}
