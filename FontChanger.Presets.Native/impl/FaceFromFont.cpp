#include "pch.h"
#include "FontChanger.Presets/FaceFromFont.h"

#include "FontChanger.Presets/CodepointRanges.h"
#include "FontChanger.Presets/DirectWriteUtil.h"
#include "FontChanger.Presets/GlyphMergingPresets.h"

using FontChanger::CodepointRanges::Ranges;
using FontChanger::FaceFromFont::FontInfo;
using FontChanger::FaceFromFont::FontOpener;
using FontChanger::Structs::FaceElement;
using FontChanger::Structs::LookupStruct;
using FontChanger::FixedSizeFont::codepoint_merge_mode;
using FontChanger::FixedSizeFont::glyph_merge_mapping;

namespace {
	const Ranges AllCodepoints{{0x20, 0x10FFFF}};
	const Ranges LatinCodepoints{{0x20, 0x2FFF}};
	const Ranges CjkCodepoints{{0x3000, 0x10FFFF}};
	const Ranges HangulCodepoints{{0x1100, 0x11FF}, {0x3130, 0x318F}, {0xAC00, 0xD7AF}};
	const Ranges HanCodepoints{{0x3400, 0x4DBF}, {0x4E00, 0x9FFF}};
	const Ranges PrivateUseCodepoints{{0xE000, 0xF8FF}};
	const Ranges DigitCodepoints{{U'0', U'9'}};
	const Ranges ExclamationMarkCodepoints{{U'!', U'!'}};

	// The game's PUA glyphs, as the Lodestone web font (FFXIV_Lodestone_SSF) draws them.
	constexpr auto LodestoneFontName = "XIV AXIS Std ATK";

	// Fonts of every Windows installation that draw the texts of merged glyphs in scripts that the chosen font lacks.
	constexpr std::array MergedTextFallbackFontNames{"Yu Gothic UI", "Malgun Gothic"};

	// JupiterN's exclamation mark of critical hits, as tuned in the game with Source Han Sans K: italic, half again as large
	// as the digits, down past the baseline, and leaning over the digit before it.
	struct MarkTraits {
		float SizePerDigitSize = 146.f / 96;
		float OffsetPerSize = -8.f / 96;
		float BaselineShiftPerSize = 15.f / 96;

		// The gap between the digits' ink and its own over the size, and the distance between the marks of "!!" over the
		// width of their ink.
		float GapPerSize = -0.256f;
		float PitchPerInk = 0.424f;
	};

	struct Traits {
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
		std::vector<std::string_view> Features;

		std::optional<MarkTraits> Mark;

		static Traits Of(std::string_view family) {
			if (family == "Jupiter")
				return {U'H', 0.773f, true, false, {"onum", "smcp"}};
			if (family == "JupiterN")
				return {U'8', 0.76f, false, false, {"onum"}, MarkTraits{}};
			if (family == "Meidinger")
				return {U'8', 0.71f, false, false, {}};
			if (family == "MiedingerMid")
				return {U'H', 0.705f, true, false, {}};
			if (family == "TrumpGothic")
				return {U'H', 0.761f, true, false, {}};
			return {U'H', 0.768f, true, true, {}};
		}
	};

	// Pixels that the game's AXIS draws glyphs to the right of and below where AXIS Basic ProN would be: Latin, CJK, and
	// PUA glyphs; at 36 px, more to the right, and CJK and PUA glyphs a pixel up. Other families' Latin glyphs are assumed
	// to move alike with the size.
	struct Placement {
		float LatinOffset;
		float CjkOffset;
		float CjkShift;
		float PuaOffset;

		static Placement Of(const Traits& traits, float size) {
			if (!traits.IsAxis)
				return {(std::max)(1.f, std::round(size / 12)), 0, 0, 0};
			if (size >= 36)
				return {3, 1, -1, 3};
			return {1, 0, 0, 1};
		}
	};

	// Returns whether the font has the characters of a text, other than line feeds and the bar of IME indicators.
	bool HasText(const FontInfo& font, std::u32string_view text) {
		if (text.starts_with(U'_'))
			text.remove_prefix(1);
		return std::ranges::all_of(text, [&font](char32_t c) { return c == U'\n' || font.GetGlyph(c); });
	}

	// Returns whether a font has tabular figures (tnum), or digits that are all as wide by default (as most fonts' are).
	bool HasTabularDigits(const FontInfo& font) {
		if (font.HasFeature("tnum"))
			return true;
		const auto zero = font.GetGlyph(U'0');
		for (auto c = U'0'; c <= U'9'; c++) {
			const auto glyph = font.GetGlyph(c);
			if (!zero || !glyph || glyph->Advance != zero->Advance)  // NOLINT(clang-diagnostic-float-equal)
				return false;
		}
		return true;
	}

	// Returns the size to draw a font at for its glyph to be as tall as wanted, and how to draw it there: natural at sizes
	// of the font's bitmaps (rounded to them), natural symmetric otherwise.
	std::pair<float, DWRITE_RENDERING_MODE> GetSizeAndMode(const FontInfo& font, float exactSize) {
		if (const auto rounded = std::round(exactSize); rounded > 0 && font.HasBitmapOf(static_cast<int>(rounded)))
			return {rounded, DWRITE_RENDERING_MODE_NATURAL};
		return {std::round(exactSize * 10) / 10, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC};
	}

	struct ExclamationMarkPlacement {
		float DigitOffset;
		float Size;
		DWRITE_RENDERING_MODE RenderMode;
		float Offset;
		float BaselineShift;
		float LetterSpacing;
	};

	// Places JupiterN's exclamation mark by the font's glyphs: the gap between the ink of the digit before it (the one
	// reaching furthest right) and its own, and the distance between the marks of "!!" over the ink's width, as in
	// proportion they are with Source Han Sans K. A gap too wide moves the digits right (within a third of an em) rather
	// than the mark left, as glyphs may not start left of their pens; too narrow, the mark right.
	ExclamationMarkPlacement PlaceExclamationMark(const MarkTraits& traits, const FontInfo& digits, const FontInfo& mark, float fontSize, float size, float latinOffset) {
		const auto [markSize, renderMode] = GetSizeAndMode(mark, fontSize * traits.SizePerDigitSize);
		auto markOffset = std::round(traits.OffsetPerSize * size);

		auto digitRight = std::numeric_limits<float>::max();
		for (auto c = U'0'; c <= U'9'; c++) {
			if (const auto glyph = digits.GetGlyph(c))
				digitRight = (std::min)(digitRight, glyph->Right);
		}
		digitRight = digitRight == std::numeric_limits<float>::max() ? 0 : digitRight * fontSize;
		const auto glyph = mark.GetGlyph(U'!').value_or(FontChanger::FaceFromFont::GlyphBox{});

		const auto excess = markOffset + glyph.Left * markSize + digitRight - latinOffset - traits.GapPerSize * size;
		const auto digitOffset = latinOffset + std::round(std::clamp(excess, 0.f, size / 3));
		if (excess < 0)
			markOffset -= std::round(excess);

		const auto inkWidth = (glyph.Advance - glyph.Left - glyph.Right) * markSize;
		return {
			.DigitOffset = digitOffset,
			.Size = markSize,
			.RenderMode = renderMode,
			.Offset = markOffset,
			.BaselineShift = std::round(traits.BaselineShiftPerSize * size),
			.LetterSpacing = std::round(traits.PitchPerInk * inkWidth - glyph.Advance * markSize),
		};
	}

	std::unique_ptr<FaceElement> Make(
		LookupStruct lookup,
		float size,
		DWRITE_RENDERING_MODE renderMode,
		float gamma,
		Ranges codepoints,
		codepoint_merge_mode mergeMode,
		float horizontalOffset = 0,
		float baselineShift = 0,
		float letterSpacing = 0) {
		lookup.Synthesis = FontChanger::Structs::SynthesisStruct{};

		auto element = std::make_unique<FaceElement>();
		element->Size = size;
		element->Gamma = gamma;
		element->MergeMode = mergeMode;
		element->Renderer = FontChanger::Structs::RendererEnum::DirectWrite;
		element->Lookup = std::move(lookup);
		element->RendererSpecific.DirectWrite.RenderMode = renderMode;
		element->RendererSpecific.DirectWrite.MeasureMode = DWRITE_MEASURING_MODE_GDI_NATURAL;
		element->RendererSpecific.DirectWrite.GridFitMode = DWRITE_GRID_FIT_MODE_ENABLED;
		element->WrapModifiers.Codepoints = std::move(codepoints);
		element->WrapModifiers.HorizontalOffset = horizontalOffset;
		element->WrapModifiers.BaselineShift = baselineShift;
		element->WrapModifiers.LetterSpacing = letterSpacing;
		return element;
	}

	// The glyph merging elements of a face of AXIS: the game's symbols and the IME indicators drawn in the font, and those
	// whose texts it lacks in the first of Windows' fonts that has them. Those whose texts no font has are left out, for
	// the glyphs of the game or of the Lodestone font to stay.
	std::vector<std::unique_ptr<FaceElement>> MakeMergedGlyphElements(const LookupStruct& chosen, const FontInfo& font, float size, const FontOpener& open) {
		std::vector<std::pair<LookupStruct, const FontInfo*>> fonts{{{.Name = chosen.Name, .Weight = chosen.Weight, .Stretch = chosen.Stretch, .Style = chosen.Style}, &font}};
		for (const auto name : MergedTextFallbackFontNames) {
			LookupStruct fallback{.Name = name};
			if (const auto info = open(fallback))
				fonts.emplace_back(std::move(fallback), info);
		}

		// Each glyph goes to the first font that has its text, in a mapping like the preset's.
		std::vector<std::vector<glyph_merge_mapping>> mappingsByFont(fonts.size());
		for (const auto& preset : FontChanger::GlyphMergingPresets::Get()) {
			for (const auto& mapping : preset.Mappings) {
				std::vector<glyph_merge_mapping*> parts(fonts.size());
				for (size_t i = 0; i < mapping.Codepoints.size() && i < mapping.Texts.size(); i++) {
					const auto f = std::ranges::find_if(fonts, [&](const auto& font) { return HasText(*font.second, mapping.Texts[i]); }) - fonts.begin();
					if (f == static_cast<ptrdiff_t>(fonts.size()))
						continue;

					if (!parts[f]) {
						parts[f] = &mappingsByFont[f].emplace_back(mapping);
						parts[f]->Codepoints.clear();
						parts[f]->Texts.clear();
					}
					parts[f]->Codepoints.push_back(mapping.Codepoints[i]);
					parts[f]->Texts.push_back(mapping.Texts[i]);
				}
			}
		}

		std::vector<std::unique_ptr<FaceElement>> res;
		for (size_t f = 0; f < fonts.size(); f++) {
			if (mappingsByFont[f].empty())
				continue;

			std::u32string codepoints;
			for (const auto& mapping : mappingsByFont[f])
				codepoints += mapping.Codepoints;

			// Replacing the glyphs that the game's font has, at the size of the game's own, as the shapes are made for.
			auto element = Make(fonts[f].first, size, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC, 1.4f, FontChanger::CodepointRanges::FromCodepoints(codepoints), codepoint_merge_mode::Replace);
			element->GlyphMerging.Params.Mappings = std::move(mappingsByFont[f]);
			res.emplace_back(std::move(element));
		}
		return res;
	}
}

FontInfo::FontInfo(IDWriteFont* font) {
	SuccessOrThrow(font->CreateFontFace(&m_face));
	DWRITE_FONT_METRICS metrics;
	m_face->GetMetrics(&metrics);
	m_unitsPerEm = metrics.designUnitsPerEm;

	// Sizes of bitmaps: ppemY of the BitmapSize records of 48 bytes after the header of EBLC or CBLC.
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

std::optional<FontInfo> FontInfo::Open(const std::string& name, DWRITE_FONT_WEIGHT weight, DWRITE_FONT_STRETCH stretch, DWRITE_FONT_STYLE style) {
	try {
		static const auto collection = [] {
			IDWriteFactoryPtr factory;
			SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)));
			IDWriteFontCollectionPtr res;
			SuccessOrThrow(factory->GetSystemFontCollection(&res));
			return res;
		}();

		UINT32 index;
		BOOL exists;
		if (FAILED(collection->FindFamilyName(xivres::util::unicode::convert<std::wstring>(name).c_str(), &index, &exists)) || !exists)
			return std::nullopt;

		IDWriteFontFamilyPtr family;
		IDWriteFontPtr font;
		SuccessOrThrow(collection->GetFontFamily(index, &family));
		SuccessOrThrow(family->GetFirstMatchingFont(weight, stretch, style, &font));
		return FontInfo(font);
	} catch (const std::exception&) {
		return std::nullopt;
	}
}

std::optional<FontChanger::FaceFromFont::GlyphBox> FontInfo::GetGlyph(char32_t codepoint) const {
	const auto c = static_cast<UINT32>(codepoint);
	UINT16 glyph = 0;
	DWRITE_GLYPH_METRICS gm;
	if (FAILED(m_face->GetGlyphIndices(&c, 1, &glyph)) || !glyph || FAILED(m_face->GetDesignGlyphMetrics(&glyph, 1, &gm, FALSE)))
		return std::nullopt;

	const auto top = gm.verticalOriginY - gm.topSideBearing;
	const auto bottom = top - (static_cast<int>(gm.advanceHeight) - gm.topSideBearing - gm.bottomSideBearing);
	return GlyphBox{
		.Left = gm.leftSideBearing / m_unitsPerEm,
		.Right = gm.rightSideBearing / m_unitsPerEm,
		.Advance = gm.advanceWidth / m_unitsPerEm,
		.Top = top / m_unitsPerEm,
		.Bottom = bottom / m_unitsPerEm,
	};
}

bool FontInfo::HasBitmapOf(int ppem) const {
	return m_bitmapSizes.contains(ppem);
}

bool FontInfo::HasFeature(std::string_view tag) const {
	const auto feature = FontChanger::DirectWriteUtil::StringToTag(tag);
	for (const auto table : {DWRITE_MAKE_OPENTYPE_TAG('G', 'S', 'U', 'B'), DWRITE_MAKE_OPENTYPE_TAG('G', 'P', 'O', 'S')}) {
		const void* data;
		UINT32 size;
		void* context;
		BOOL exists;
		if (FAILED(m_face->TryGetFontTable(table, &data, &size, &context, &exists)) || !exists)
			continue;

		// The header: version (4 bytes), script list offset, feature list offset; the feature list: a count, then records
		// of a tag (4 bytes) and an offset.
		const auto p = static_cast<const uint8_t*>(data);
		auto found = false;
		if (size >= 10) {
			const auto list = static_cast<uint32_t>(p[6]) << 8 | p[7];
			const auto count = list + 2 <= size ? static_cast<uint32_t>(p[list]) << 8 | p[list + 1] : 0;
			for (uint32_t i = 0; i < count && list + 2 + 6 * (i + 1) <= size && !found; i++) {
				const auto record = p + list + 2 + 6 * i;
				found = (record[0] | record[1] << 8 | record[2] << 16 | static_cast<uint32_t>(record[3]) << 24) == feature;
			}
		}
		m_face->ReleaseFontTable(context);
		if (found)
			return true;
	}
	return false;
}

std::optional<std::pair<std::string_view, float>> FontChanger::FaceFromFont::GetGameFontFamilyAndSize(std::string_view faceName) {
	if (const auto def = FontChanger::FixedSizeFont::find_fontdata_definition(faceName))
		return std::make_pair(std::string_view(def->Family), def->Size);
	return std::nullopt;
}

std::optional<float> FontChanger::FaceFromFont::GetGameFontSize(const Structs::Face& face) {
	for (const auto& element : face.Elements) {
		if (element->Renderer == Structs::RendererEnum::PrerenderedGameInstallation)
			return element->Size;
	}
	if (const auto familyAndSize = GetGameFontFamilyAndSize(face.Name))
		return familyAndSize->second;
	return std::nullopt;
}

LookupStruct FontChanger::FaceFromFont::GetLookupFromLogFont(const LOGFONTW& logFont) {
	IDWriteFactoryPtr factory;
	SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)));
	IDWriteGdiInteropPtr interop;
	SuccessOrThrow(factory->GetGdiInterop(&interop));
	IDWriteFontPtr font;
	SuccessOrThrow(interop->CreateFontFromLOGFONT(&logFont, &font));
	IDWriteFontFamilyPtr family;
	SuccessOrThrow(font->GetFontFamily(&family));

	return {
		.Name = xivres::util::unicode::convert<std::string>(DirectWriteUtil::GetEnglishFamilyName(family)),
		.Weight = font->GetWeight(),
		.Stretch = font->GetStretch(),
		.Style = font->GetStyle(),
		.Synthesis = Structs::SynthesisStruct{},
	};
}

std::vector<std::unique_ptr<FaceElement>> FontChanger::FaceFromFont::MakeElements(
	std::string_view family, float size, const LookupStruct& chosen, const FontInfo& font, const FontOpener& open, bool monospacedDigits) {
	const auto traits = Traits::Of(family);
	auto lookup = chosen;
	if (monospacedDigits)
		lookup.Features[DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES] = 1;
	for (const auto feature : traits.Features)
		lookup.Features[static_cast<DWRITE_FONT_FEATURE_TAG>(DirectWriteUtil::StringToTag(feature))] = 1;

	const auto reference = font.GetGlyph(traits.ReferenceGlyph);
	const auto inkHeight = reference ? reference->Top - (std::max)(0.f, reference->Bottom) : 0.f;
	const auto [fontSize, renderMode] = GetSizeAndMode(font, size * traits.ReferenceHeight / (inkHeight > 0 ? inkHeight : traits.ReferenceHeight));
	const auto placement = Placement::Of(traits, size);
	auto latinOffset = placement.LatinOffset;

	// JupiterN's digits are moved right, toward the exclamation mark after them.
	auto italic = chosen;
	italic.Style = DWRITE_FONT_STYLE_ITALIC;
	std::optional<ExclamationMarkPlacement> mark;
	if (traits.Mark) {
		const auto italicFont = open(italic);
		mark = PlaceExclamationMark(*traits.Mark, font, italicFont ? *italicFont : font, fontSize, size, latinOffset);
		latinOffset = mark->DigitOffset;
	}

	const auto hasKana = HasText(font, U"あア");
	const auto hasHangul = HasText(font, U"가");
	const auto hasHan = HasText(font, U"中");

	// Fonts made for Chinese, which have simplified characters, add the Han characters that the game's fonts lack.
	const auto isChinese = HasText(font, U"们");

	std::vector<std::unique_ptr<FaceElement>> res;
	if (traits.HasText && hasHangul)
		res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, HangulCodepoints, codepoint_merge_mode::AddAll, placement.CjkOffset, placement.CjkShift));
	if (traits.HasText && isChinese)
		res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, HanCodepoints, codepoint_merge_mode::AddAll, placement.CjkOffset, placement.CjkShift));

	// The glyphs that the game's font has; in AXIS, CJK characters are placed apart from Latin ones, as the game does.
	if (traits.IsAxis && (hasKana || hasHangul || hasHan)) {
		res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, LatinCodepoints, codepoint_merge_mode::Replace, latinOffset));
		res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, CjkCodepoints, codepoint_merge_mode::Replace, placement.CjkOffset, placement.CjkShift));
	} else {
		res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, AllCodepoints, codepoint_merge_mode::Replace, latinOffset));
	}

	// Digits of a font without tabular figures: each in a cell as wide as its 0, centered.
	if (monospacedDigits && !HasTabularDigits(font)) {
		auto& digits = res.emplace_back(Make(lookup, fontSize, renderMode, 1.2f, DigitCodepoints, codepoint_merge_mode::Replace, latinOffset));
		digits->WrapModifiers.Monospacing = {
			.MinAdvance = 1.f,
			.MaxAdvance = 1.f,
			.Unit = FontChanger::FixedSizeFont::monospacing_unit::ReferenceGlyph,
			.ReferenceCharacter = U'0',
			.Alignment = FontChanger::FixedSizeFont::monospacing_alignment::CenterAdvance,
		};
	}

	// JupiterN's exclamation mark of critical hits: italic, half again as large as the digits, down past the baseline,
	// leaning over the digit before it, and the digit after it close.
	if (mark)
		res.emplace_back(Make(italic, mark->Size, mark->RenderMode, 1.2f, ExclamationMarkCodepoints, codepoint_merge_mode::Replace, mark->Offset, mark->BaselineShift, mark->LetterSpacing));

	if (traits.IsAxis) {
		// The game's PUA glyphs as vectors, so that they scale alike at sizes that the game has no font of; after the
		// font's elements, which decide how the face is measured.
		res.emplace_back(Make({.Name = LodestoneFontName}, size, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC, 1.4f, PrivateUseCodepoints, codepoint_merge_mode::Replace, placement.PuaOffset, placement.CjkShift));

		auto merged = MakeMergedGlyphElements(chosen, font, size, open);
		res.insert(res.end(), std::make_move_iterator(merged.begin()), std::make_move_iterator(merged.end()));
	}

	return res;
}

std::vector<std::pair<FontChanger::Structs::Face*, std::vector<std::unique_ptr<FaceElement>>>> FontChanger::FaceFromFont::MakeFamilyElements(
	const Structs::FontSet& fontSet, std::string_view family, const LookupStruct& chosen) {
	// Each font is opened once for all the faces.
	std::map<std::tuple<std::string, DWRITE_FONT_WEIGHT, DWRITE_FONT_STRETCH, DWRITE_FONT_STYLE>, std::optional<FontInfo>> fonts;
	const FontOpener open = [&fonts](const LookupStruct& lookup) -> const FontInfo* {
		const auto key = std::make_tuple(lookup.Name, lookup.Weight, lookup.Stretch, lookup.Style);
		auto it = fonts.find(key);
		if (it == fonts.end())
			it = fonts.emplace(key, FontInfo::Open(lookup.Name, lookup.Weight, lookup.Stretch, lookup.Style)).first;
		return it->second ? &*it->second : nullptr;
	};

	const auto font = open(chosen);
	if (!font)
		throw std::runtime_error(std::format("Font not found: {}", chosen.Name));

	std::vector<std::pair<Structs::Face*, std::vector<std::unique_ptr<FaceElement>>>> res;
	for (const auto& pFace : fontSet.Faces) {
		const auto other = GetGameFontFamilyAndSize(pFace->Name);
		const auto size = GetGameFontSize(*pFace);
		if (other && size && other->first == family)
			res.emplace_back(pFace.get(), MakeElements(family, *size, chosen, *font, open));
	}
	return res;
}
