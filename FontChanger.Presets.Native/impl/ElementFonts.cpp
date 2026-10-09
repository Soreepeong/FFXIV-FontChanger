#include "pch.h"
#include "FontChanger.Presets/ElementFonts.h"

#include "FontChanger.Presets/DirectWriteUtil.h"
#include "FontChanger.Presets/GlyphFiles.h"

static std::map<xivres::font_type, FontChanger::FixedSizeFont::game_fontdata_set> s_fontSetCache;
static std::mutex s_fontSetCacheMtx;
static bool s_showedGameNotFoundError = false;
static std::function<std::vector<std::filesystem::path>(xivres::font_type)> s_gameInstallationPathsProvider;
static std::function<void(const std::exception&)> s_gameFontErrorHandler;

// Gets a game font by the name of its family (as elements of game fonts look them up by) closest to a size, from the
// first font type that has the family.
static std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> GetGameFont(const std::string& name, float size) {
	const auto types = FontChanger::FixedSizeFont::get_font_types();
	const auto type = std::ranges::find_if(types, [&](const auto t) {
		const auto defs = FontChanger::FixedSizeFont::get_fontdata_definition(t);
		return std::ranges::find(defs, name, &FontChanger::FixedSizeFont::game_fontdata_definition::Family) != defs.end();
	});
	if (type == types.end())
		throw std::runtime_error("Invalid name");

	const auto lock = std::scoped_lock(s_fontSetCacheMtx);
	try {
		const auto fontType = *type;
		auto& font = s_fontSetCache[fontType];
		if (!font) {
			if (s_gameInstallationPathsProvider) {
				for (const auto& path : s_gameInstallationPathsProvider(fontType)) {
					try {
						font = FontChanger::FixedSizeFont::get_fontdata_set(xivres::installation(path), fontType);
						break;
					} catch (...) {}
				}
			}
			if (!font)
				throw std::runtime_error("Font not found in given path");
		}
		return font.get_font(name, size);
	} catch (const std::exception& e) {
		if (s_gameFontErrorHandler && !s_showedGameNotFoundError) {
			s_showedGameNotFoundError = true;
			s_gameFontErrorHandler(e);
		}
	}

	return std::make_shared<FontChanger::FixedSizeFont::empty_fixed_size_font>(size, FontChanger::FixedSizeFont::empty_fixed_size_font::create_struct{});
}

// Returns the font without simulations that a simulated font is made from.
static IDWriteFontPtr GetRealFont(IDWriteFactory* factory, IDWriteFont* font) {
	if (font->GetSimulations() == DWRITE_FONT_SIMULATIONS_NONE)
		return font;

	IDWriteFontFacePtr face;
	SuccessOrThrow(font->CreateFontFace(&face));

	IDWriteFontFile* pFontFileTmp;
	uint32_t nFiles = 1;
	SuccessOrThrow(face->GetFiles(&nFiles, &pFontFileTmp));
	IDWriteFontFilePtr file(pFontFileTmp, false);

	IDWriteFontFacePtr realFace;
	SuccessOrThrow(factory->CreateFontFace(face->GetType(), 1, &pFontFileTmp, face->GetIndex(), DWRITE_FONT_SIMULATIONS_NONE, &realFace));

	IDWriteFontFamilyPtr family;
	SuccessOrThrow(font->GetFontFamily(&family));
	IDWriteFontCollectionPtr collection;
	SuccessOrThrow(family->GetFontCollection(&collection));
	IDWriteFontPtr realFont;
	SuccessOrThrow(collection->GetFontFromFontFace(realFace, &realFont));
	return realFont;
}

std::pair<IDWriteFactoryPtr, IDWriteFontPtr> FontChanger::ElementFonts::ResolveFont(const Structs::LookupStruct& lookup) {
	IDWriteFactoryPtr factory;
	// Fonts of the shared factory share their faces, and with them what DirectWrite keeps of the glyphs that it drew, so
	// that the bounds and pixels of a glyph could differ by a pixel depending on what other fonts of the same face drew
	// before, on any thread; exports then differed from run to run. Each font gets a factory of its own instead.
	SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)));

	IDWriteFontCollectionPtr coll;
	SuccessOrThrow(factory->GetSystemFontCollection(&coll));

	uint32_t index;
	BOOL exists;
	SuccessOrThrow(coll->FindFamilyName(xivres::util::unicode::convert<std::wstring>(lookup.Name).c_str(), &index, &exists));
	if (!exists)
		throw std::invalid_argument("Font not found");

	IDWriteFontFamilyPtr family;
	SuccessOrThrow(coll->GetFontFamily(index, &family));

	IDWriteFontPtr font;
	SuccessOrThrow(family->GetFirstMatchingFont(lookup.Weight, lookup.Stretch, lookup.Style, &font));

	// DirectWrite may match a simulated font, which is left to synthesis; take the real face that it is made from.
	if (lookup.Synthesis)
		font = GetRealFont(factory, font);

	return std::make_pair(std::move(factory), std::move(font));
}

// Returns the font file, the index of the face in it, and the axis values of the instance if it is a variable font.
static std::tuple<std::vector<uint8_t>, int, std::map<uint32_t, float>> ReadFontFile(IDWriteFont* font) {
	IDWriteFontFacePtr face;
	SuccessOrThrow(font->CreateFontFace(&face));

	IDWriteFontFile* pFontFileTmp;
	uint32_t nFiles = 1;
	SuccessOrThrow(face->GetFiles(&nFiles, &pFontFileTmp));
	IDWriteFontFilePtr file(pFontFileTmp, false);

	IDWriteFontFileLoaderPtr loader;
	SuccessOrThrow(file->GetLoader(&loader));

	void const* refKey;
	UINT32 refKeySize;
	SuccessOrThrow(file->GetReferenceKey(&refKey, &refKeySize));

	IDWriteFontFileStreamPtr stream;
	SuccessOrThrow(loader->CreateStreamFromKey(refKey, refKeySize, &stream));

	uint64_t fileSize;
	SuccessOrThrow(stream->GetFileSize(&fileSize));
	const void* pFragmentStart;
	void* pFragmentContext;
	SuccessOrThrow(stream->ReadFileFragment(&pFragmentStart, 0, fileSize, &pFragmentContext));
	std::vector<uint8_t> buf(fileSize);
	memcpy(buf.data(), pFragmentStart, buf.size());
	stream->ReleaseFileFragment(pFragmentContext);

	// DirectWrite lists the named instances of variable fonts as separate fonts; the face index does not tell them apart.
	// The optical size is left out, so that it can follow the font size.
	std::map<uint32_t, float> instanceAxisValues;
	if (IDWriteFontFace5Ptr face5; SUCCEEDED(face.QueryInterface(decltype(face5)::GetIID(), &face5)) && face5->HasVariations()) {
		std::vector<DWRITE_FONT_AXIS_VALUE> axisValues(face5->GetFontAxisValueCount());
		SuccessOrThrow(face5->GetFontAxisValues(axisValues.data(), static_cast<UINT32>(axisValues.size())));
		for (const auto& axisValue : axisValues) {
			if (axisValue.axisTag != DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE)
				instanceAxisValues.emplace(static_cast<uint32_t>(axisValue.axisTag), axisValue.value);
		}
	}

	return {std::move(buf), face->GetIndex(), std::move(instanceAxisValues)};
}

std::tuple<std::shared_ptr<xivres::stream>, int, std::map<uint32_t, float>> FontChanger::ElementFonts::ResolveStream(const Structs::LookupStruct& lookup) {
	auto [data, index, instanceAxisValues] = ReadFontFile(ResolveFont(lookup).second);
	return {std::make_shared<xivres::memory_stream>(std::move(data)), index, std::move(instanceAxisValues)};
}

float FontChanger::ElementFonts::GetStretchPercent(DWRITE_FONT_STRETCH stretch) {
	switch (stretch) {
		case DWRITE_FONT_STRETCH_ULTRA_CONDENSED: return 50.f;
		case DWRITE_FONT_STRETCH_EXTRA_CONDENSED: return 62.5f;
		case DWRITE_FONT_STRETCH_CONDENSED: return 75.f;
		case DWRITE_FONT_STRETCH_SEMI_CONDENSED: return 87.5f;
		case DWRITE_FONT_STRETCH_NORMAL: return 100.f;
		case DWRITE_FONT_STRETCH_SEMI_EXPANDED: return 112.5f;
		case DWRITE_FONT_STRETCH_EXPANDED: return 125.f;
		case DWRITE_FONT_STRETCH_EXTRA_EXPANDED: return 150.f;
		case DWRITE_FONT_STRETCH_ULTRA_EXPANDED: return 200.f;
		default: return 0.f;
	}
}

FontChanger::FixedSizeFont::font_render_transformation_matrix FontChanger::ElementFonts::SynthesizedFace::GetScreenMatrix() const {
	// x' = ScaleX (x - slope y), where y grows downwards: points above the baseline move to the right.
	const auto slope = Oblique ? ObliqueSlope : 0.f;
	return {ScaleX, -slope * ScaleX, 0.f, 1.f};
}

float FontChanger::ElementFonts::SynthesizedFace::GetEmbolden() const {
	// The bold simulation of DirectWrite makes Arial, Segoe UI, and Times New Roman 1/50 em wider and taller, and
	// advance 1/50 em further, as measured from GetGlyphRunOutline and GetDesignGlyphMetrics (41/2048 em wider and
	// 40/2048 em taller); going from 400 to 700 is taken to be that, and other weights in proportion.
	return static_cast<float>(WeightDelta) / 300.f / 50.f;
}

FontChanger::ElementFonts::SynthesizedFace FontChanger::ElementFonts::ResolveSynthesis(const Structs::LookupStruct& lookup, Structs::RendererEnum renderer, IDWriteFont* font) {
	using Structs::RendererEnum;
	SynthesizedFace res;
	if (!lookup.Synthesis || !font)
		return res;

	auto simulations = DWRITE_FONT_SIMULATIONS_NONE;
	auto realWeight = static_cast<int>(font->GetWeight());
	auto realStyle = font->GetStyle();
	auto realStretchPercent = GetStretchPercent(font->GetStretch());

	// Variable fonts get the requested properties from their axes where they can; the rest is synthesized. Axes that
	// Variations set are left as the user set them, and so are the properties that they give.
	if (IDWriteFontFacePtr face; SUCCEEDED(font->CreateFontFace(&face))) {
		if (IDWriteFontFace5Ptr face5; SUCCEEDED(face.QueryInterface(decltype(face5)::GetIID(), &face5)) && face5->HasVariations()) {
			IDWriteFontResourcePtr resource;
			SuccessOrThrow(face5->GetFontResource(&resource));
			std::vector<DWRITE_FONT_AXIS_RANGE> ranges(resource->GetFontAxisCount());
			SuccessOrThrow(resource->GetFontAxisRanges(ranges.data(), static_cast<UINT32>(ranges.size())));

			const auto userAxes = lookup.GetVariationAxisValues();
			for (const auto& range : ranges) {
				const auto tag = static_cast<uint32_t>(range.axisTag);
				const auto userSet = userAxes.contains(tag);
				switch (range.axisTag) {
					case DWRITE_FONT_AXIS_TAG_WEIGHT:
						if (userSet) {
							realWeight = static_cast<int>(lookup.Weight);
						} else {
							const auto value = std::clamp(static_cast<float>(lookup.Weight), range.minValue, range.maxValue);
							res.AxisValues[tag] = value;
							realWeight = static_cast<int>(std::lround(value));
						}
						break;

					case DWRITE_FONT_AXIS_TAG_WIDTH:
						if (userSet) {
							realStretchPercent = GetStretchPercent(lookup.Stretch);
						} else if (const auto requested = GetStretchPercent(lookup.Stretch); requested > 0) {
							const auto value = std::clamp(requested, range.minValue, range.maxValue);
							res.AxisValues[tag] = value;
							realStretchPercent = value;
						}
						break;

					case DWRITE_FONT_AXIS_TAG_ITALIC:
						if (userSet) {
							realStyle = lookup.Style;
						} else if (lookup.Style != DWRITE_FONT_STYLE_NORMAL && range.maxValue >= 1.f) {
							res.AxisValues[tag] = 1.f;
							realStyle = DWRITE_FONT_STYLE_ITALIC;
						}
						break;

					case DWRITE_FONT_AXIS_TAG_SLANT:
						// Negative values lean to the right; as far as the slant of the oblique simulation, if the font goes that far.
						if (userSet) {
							realStyle = lookup.Style;
						} else if (lookup.Style != DWRITE_FONT_STYLE_NORMAL && range.minValue < 0.f && realStyle == DWRITE_FONT_STYLE_NORMAL) {
							constexpr auto RadiansToDegrees = 180.f / std::numbers::pi_v<float>;
							res.AxisValues[tag] = (std::max)(range.minValue, -std::atan(SynthesizedFace::ObliqueSlope) * RadiansToDegrees);
							realStyle = DWRITE_FONT_STYLE_OBLIQUE;
						}
						break;
				}
			}
		}
	}

	if (lookup.Synthesis->Allow) {
		// DirectWrite can only make a bold face, and only from faces that are not bold already.
		if (renderer == RendererEnum::FreeType)
			res.WeightDelta = static_cast<int>(lookup.Weight) - realWeight;
		else if (lookup.Weight >= DWRITE_FONT_WEIGHT_SEMI_BOLD && realWeight <= DWRITE_FONT_WEIGHT_MEDIUM)
			simulations = static_cast<DWRITE_FONT_SIMULATIONS>(simulations | DWRITE_FONT_SIMULATIONS_BOLD);

		if (lookup.Style != DWRITE_FONT_STYLE_NORMAL && realStyle == DWRITE_FONT_STYLE_NORMAL) {
			if (renderer == RendererEnum::FreeType)
				res.Oblique = true;
			else
				simulations = static_cast<DWRITE_FONT_SIMULATIONS>(simulations | DWRITE_FONT_SIMULATIONS_OBLIQUE);
		}

		if (const auto requested = GetStretchPercent(lookup.Stretch); requested > 0 && realStretchPercent > 0 && requested != realStretchPercent)  // NOLINT(clang-diagnostic-float-equal)
			res.ScaleX = requested / realStretchPercent;
	}

	res.Simulations = simulations;
	return res;
}

void FontChanger::ElementFonts::ConvertToExplicitSynthesis(Structs::LookupStruct& lookup, Structs::RendererEnum renderer) {
	using Structs::RendererEnum;
	if (lookup.Synthesis || (renderer != RendererEnum::DirectWrite && renderer != RendererEnum::FreeType))
		return;

	try {
		const auto [factory, font] = ResolveFont(lookup);
		const auto simulations = font->GetSimulations();
		const auto realFont = GetRealFont(factory, font);

		// Ask for the real face, and for DirectWrite, for what makes the same simulations; FreeType did not simulate.
		lookup.Weight = realFont->GetWeight();
		lookup.Style = realFont->GetStyle();
		lookup.Stretch = realFont->GetStretch();
		if (renderer == RendererEnum::DirectWrite) {
			if (simulations & DWRITE_FONT_SIMULATIONS_BOLD)
				lookup.Weight = DWRITE_FONT_WEIGHT_BOLD;
			if ((simulations & DWRITE_FONT_SIMULATIONS_OBLIQUE) && lookup.Style == DWRITE_FONT_STYLE_NORMAL)
				lookup.Style = DWRITE_FONT_STYLE_ITALIC;
		}
	} catch (...) {
		// The font is not installed; keep the requested properties.
	}

	lookup.Synthesis = Structs::SynthesisStruct{};
}

namespace {
	using FontChanger::FixedSizeFont::font_render_transformation_matrix;
	using FontChanger::Structs::TransformStruct;

	std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> CreateRendererFont(
		FontChanger::Structs::RendererEnum renderer,
		const FontChanger::Structs::LookupStruct& lookup,
		const FontChanger::Structs::RendererSpecificStruct& rendererSpecific,
		float size,
		float gamma,
		const font_render_transformation_matrix& screenMatrix) {
		auto [factory, font] = FontChanger::ElementFonts::ResolveFont(lookup);

		// The synthesis is part of the face: the glyphs are slanted and scaled by it before the given transformation.
		const auto synthesis = FontChanger::ElementFonts::ResolveSynthesis(lookup, renderer, font);
		const auto rendererMatrix = TransformStruct::ScreenToRenderer(renderer, TransformStruct::Multiply(screenMatrix, synthesis.GetScreenMatrix()));

		switch (renderer) {
			case FontChanger::Structs::RendererEnum::DirectWrite: {
				auto specifics = rendererSpecific.DirectWrite;
				specifics.Features.clear();
				for (const auto& [tag, value] : lookup.Features)
					specifics.Features.push_back({ .nameTag = tag, .parameter = value });
				specifics.Language = lookup.Language;
				specifics.Variations = synthesis.AxisValues;
				for (const auto& [tag, value] : lookup.GetVariationAxisValues())
					specifics.Variations[tag] = value;
				specifics.Simulations = synthesis.Simulations;
				return std::make_shared<FontChanger::FixedSizeFont::directwrite_fixed_size_font>(std::move(factory), std::move(font), size, gamma, rendererMatrix, specifics);
			}

			case FontChanger::Structs::RendererEnum::FreeType: {
				auto [data, index, instanceAxisValues] = ReadFontFile(font);
				auto specifics = rendererSpecific.FreeType;
				specifics.Features.clear();
				for (const auto& [tag, value] : lookup.Features)
					specifics.Features.push_back({.tag = _byteswap_ulong(tag), .value = value, .start = HB_FEATURE_GLOBAL_START, .end = HB_FEATURE_GLOBAL_END});
				specifics.Language = lookup.Language;
				specifics.Variations = std::move(instanceAxisValues);
				for (const auto& [tag, value] : synthesis.AxisValues)
					specifics.Variations[tag] = value;
				for (const auto& [tag, value] : lookup.GetVariationAxisValues())
					specifics.Variations[tag] = value;
				specifics.Embolden = synthesis.GetEmbolden();
				return std::make_shared<FontChanger::FixedSizeFont::freetype_fixed_size_font>(std::move(data), index, size, gamma, rendererMatrix, specifics);
			}

			default:
				throw std::invalid_argument("Renderer does not draw from font files");
		}
	}

	// Returns the transformation on screen of the texts of glyph merging: by the element's transformation, then the text's,
	// and then by the condensing.
	font_render_transformation_matrix ComposeTextMatrix(
		const FontChanger::Structs::TransformStruct& element,
		const FontChanger::Structs::TransformStruct& text,
		float condense) {
		const font_render_transformation_matrix condensing{condense, 0.f, 0.f, 1.f};
		return TransformStruct::Multiply(condensing, TransformStruct::Multiply(text.GetScreenMatrix(), element.GetScreenMatrix()));
	}
}

const std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>& FontChanger::Structs::FaceElement::GetBaseFont() const {
	if (!m_baseFont) {
		try {
			switch (Renderer) {
				case RendererEnum::Empty:
					m_baseFont = std::make_shared<FontChanger::FixedSizeFont::empty_fixed_size_font>(Size, RendererSpecific.Empty);
					break;

				case RendererEnum::PrerenderedGameInstallation:
					m_baseFont = GetGameFont(Lookup.Name, Size);
					break;

				case RendererEnum::DirectWrite:
				case RendererEnum::FreeType: {
					auto font = CreateRendererFont(Renderer, Lookup, RendererSpecific, Size, Gamma, Transform.GetScreenMatrix());
					if (!GlyphMerging.IsEnabled()) {
						m_baseFont = std::move(font);
						break;
					}

					m_baseFont = std::make_shared<FontChanger::FixedSizeFont::glyph_merging_fixed_size_font>(
						std::move(font),
						GlyphMerging.Params,
						[renderer = Renderer, lookup = Lookup, rendererSpecific = RendererSpecific, gamma = Gamma, elementTransform = Transform, textTransform = GlyphMerging.TextTransform](float size, float condense) {
							return CreateRendererFont(renderer, lookup, rendererSpecific, size, gamma, ComposeTextMatrix(elementTransform, textTransform, condense));
						});
					break;
				}

				case RendererEnum::GlyphImages: {
					auto font = GlyphFiles::CreateFont(RendererSpecific.GlyphImages, Size, Gamma, Transform.GetScreenMatrix());
					if (!GlyphMerging.IsEnabled()) {
						m_baseFont = std::move(font);
						break;
					}

					// Texts are drawn with the font that the lookup names, or with the glyph files themselves if it names none.
					m_baseFont = std::make_shared<FontChanger::FixedSizeFont::glyph_merging_fixed_size_font>(
						std::move(font),
						GlyphMerging.Params,
						[lookup = Lookup, rendererSpecific = RendererSpecific, gamma = Gamma, elementTransform = Transform, textTransform = GlyphMerging.TextTransform](float size, float condense) -> std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> {
							if (!lookup.Name.empty()) {
								try {
									return CreateRendererFont(RendererEnum::DirectWrite, lookup, rendererSpecific, size, gamma, ComposeTextMatrix(elementTransform, textTransform, condense));
								} catch (...) {
									// fall back to the glyph files
								}
							}
							return GlyphFiles::CreateFont(rendererSpecific.GlyphImages, size, gamma, ComposeTextMatrix(elementTransform, textTransform, condense));
						});
					break;
				}

				default:
					m_baseFont = std::make_shared<FontChanger::FixedSizeFont::empty_fixed_size_font>();
					break;
			}
		} catch (...) {
			m_baseFont = std::make_shared<FontChanger::FixedSizeFont::empty_fixed_size_font>(Size, RendererSpecific.Empty);
		}
	}

	return m_baseFont;
}


const std::shared_ptr<FontChanger::FixedSizeFont::wrapping_fixed_size_font>& FontChanger::Structs::FaceElement::GetWrappedFont() const {
	if (!m_wrappedFont) {
		// Glyphs squeezed by monospacing are rendered narrower by the renderer, which keeps their hinting and stroke weight.
		// Fonts of glyph merging have glyphs that the renderer alone cannot make, so their glyphs are resampled instead.
		FontChanger::FixedSizeFont::wrapping_fixed_size_font::scaled_font_factory scaledFontFactory;
		if ((Renderer == RendererEnum::DirectWrite || Renderer == RendererEnum::FreeType) && !GlyphMerging.IsEnabled() && WrapModifiers.Monospacing.is_enabled()) {
			scaledFontFactory = [renderer = Renderer, lookup = Lookup, rendererSpecific = RendererSpecific, size = Size, gamma = Gamma, transform = Transform](float scaleX) {
				const FontChanger::FixedSizeFont::font_render_transformation_matrix scaling{scaleX, 0.f, 0.f, 1.f};
				return CreateRendererFont(renderer, lookup, rendererSpecific, size, gamma, TransformStruct::Multiply(scaling, transform.GetScreenMatrix()));
			};
		}
		m_wrappedFont = std::make_shared<FontChanger::FixedSizeFont::wrapping_fixed_size_font>(GetBaseFont(), WrapModifiers, std::move(scaledFontFactory));
	}

	return m_wrappedFont;
}

void FontChanger::Structs::FaceElement::OnFontWrappingParametersChange() {
	m_wrappedFont = nullptr;
}

void FontChanger::Structs::FaceElement::OnFontCreateParametersChange() {
	m_wrappedFont = nullptr;
	m_baseFont = nullptr;
}

std::string FontChanger::Structs::FaceElement::GetBaseFontKey() const {
	// All that the base font is made from: all but how the wrapped font picks and moves its glyphs. Glyph files at relative
	// paths are told apart by where they are.
	auto json = nlohmann::json(*this);
	json.erase("mergeMode");
	json.erase("wrapModifiers");
	if (auto& renderSpecific = json["renderSpecific"]; renderSpecific.contains("glyphImages"))
		renderSpecific["glyphImages"]["path"] = xivres::util::unicode::convert<std::string>(GlyphFiles::ResolvePath(RendererSpecific.GlyphImages.Path).wstring());
	return json.dump();
}

std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> FontChanger::ElementFonts::MergeElements(const Structs::Face& face, const std::set<const Structs::FaceElement*>& skip) {
	std::vector<std::pair<std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>, FontChanger::FixedSizeFont::codepoint_merge_mode>> mergeFontList;
	for (const auto& pElement : face.Elements) {
		if (!skip.contains(pElement.get()))
			mergeFontList.emplace_back(pElement->GetWrappedFont(), pElement->MergeMode);
	}
	return std::make_shared<FontChanger::FixedSizeFont::merged_fixed_size_font>(std::move(mergeFontList), face.VerticalAlignment);
}

const std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>& FontChanger::Structs::Face::GetMergedFont() const {
	if (!MergedFont)
		MergedFont = ElementFonts::MergeElements(*this);
	return MergedFont;
}

void FontChanger::Structs::Face::FlushCache() {
	OnElementChange();
	for (const auto& e : Elements)
		e->OnFontCreateParametersChange();
}

void FontChanger::Structs::Face::OnElementChange() {
	MergedFont = nullptr;
}

void FontChanger::Structs::FontSet::FlushCache() {
	for (const auto& e : Faces)
		e->FlushCache();
}

void FontChanger::Structs::FontSet::ConsolidateFonts() const {
	std::map<std::string, std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font>> loadedBaseFonts;
	for (const auto& pFace : Faces) {
		for (const auto& pElem : pFace->Elements) {
			auto& elem = *pElem;
			auto& known = loadedBaseFonts[elem.GetBaseFontKey()];
			if (known) {
				elem.m_baseFont = known;
			} else if (elem.m_baseFont) {
				known = elem.m_baseFont;
			} else {
				known = elem.GetBaseFont();
			}
			elem.OnFontWrappingParametersChange();
		}

		pFace->OnElementChange();
	}
}

void FontChanger::Structs::MultiFontSet::FlushCache() {
	for (const auto& e : FontSets)
		e->FlushCache();
}

void FontChanger::ElementFonts::SetGameInstallationPathsProvider(std::function<std::vector<std::filesystem::path>(xivres::font_type fontType)> provider) {
	const auto lock = std::scoped_lock(s_fontSetCacheMtx);
	s_gameInstallationPathsProvider = std::move(provider);
}

void FontChanger::ElementFonts::SetGameFontErrorHandler(std::function<void(const std::exception& e)> handler) {
	const auto lock = std::scoped_lock(s_fontSetCacheMtx);
	s_gameFontErrorHandler = std::move(handler);
}

void FontChanger::ElementFonts::FlushCachedFonts() {
	const auto lock = std::scoped_lock(s_fontSetCacheMtx);
	s_fontSetCache.clear();
	s_showedGameNotFoundError = false;
}

static std::filesystem::path s_projectDirectory;
static std::mutex s_projectDirectoryMtx;

void FontChanger::ElementFonts::SetProjectDirectory(std::filesystem::path path) {
	const auto lock = std::scoped_lock(s_projectDirectoryMtx);
	s_projectDirectory = std::move(path);
}

std::filesystem::path FontChanger::ElementFonts::GetProjectDirectory() {
	const auto lock = std::scoped_lock(s_projectDirectoryMtx);
	return s_projectDirectory;
}

void FontChanger::ElementFonts::OnProjectDirectoryChange(const Structs::MultiFontSet& multiFontSet) {
	for (const auto& fontSet : multiFontSet.FontSets) {
		for (const auto& face : fontSet->Faces) {
			auto changed = false;
			for (const auto& element : face->Elements) {
				if (element->UsesProjectDirectory()) {
					element->OnFontCreateParametersChange();
					changed = true;
				}
			}
			if (changed)
				face->OnElementChange();
		}
	}
}

