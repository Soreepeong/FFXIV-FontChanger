#include "pch.h"
#include "Structs.h"

#include <numbers>

#include "FontGeneratorConfig.h"
#include "GlyphFiles.h"
#include "resource.h"

static std::map<xivres::font_type, xivres::fontgen::game_fontdata_set> s_fontSetCache;
static std::mutex s_fontSetCacheMtx;
static bool s_showedGameNotFoundError = false;

// Runs without a window, such as from the command line, must not wait on dialogs.
static bool s_gameNotFoundDialogsEnabled = true;

// Writes the float with the fewest digits that read back as the same float, such as 7.0000005 instead of
// 7.000000476837158 as the double that it widens to.
static nlohmann::json FloatToJson(float value) {
	char buf[32];
	const auto [end, ec] = std::to_chars(buf, buf + sizeof buf, value);
	if (ec != std::errc{})
		return value;
	return std::strtod(std::string(buf, end).c_str(), nullptr);
}

// Values in pixels that earlier versions stored as integers are written as integers when they are whole numbers, so
// that saving files of earlier versions does not change these values in them.
static nlohmann::json PixelValueToJson(float value) {
	if (const auto rounded = std::round(value); rounded == value && std::fabs(rounded) < 1e9f)
		return static_cast<int>(rounded);
	return FloatToJson(value);
}

static std::shared_ptr<xivres::fontgen::fixed_size_font> GetGameFont(xivres::fontgen::game_font_family family, float size) {
	const auto lock = std::scoped_lock(s_fontSetCacheMtx);

	std::shared_ptr<xivres::fontgen::game_fontdata_set> strong;

	try {
		switch (family) {
			case xivres::fontgen::game_font_family::AXIS:
			case xivres::fontgen::game_font_family::Jupiter:
			case xivres::fontgen::game_font_family::JupiterN:
			case xivres::fontgen::game_font_family::MiedingerMid:
			case xivres::fontgen::game_font_family::Meidinger:
			case xivres::fontgen::game_font_family::TrumpGothic: {
				auto& font = s_fontSetCache[xivres::font_type::font];
				if (!font) {
					for (const auto pathList : { g_config.Global, g_config.China, g_config.Korea, g_config.TraditionalChinese }) {
						for (const auto& path : pathList) {
							try {
								font = xivres::installation(path).get_fontdata_set(xivres::font_type::font);
								break;
							} catch (...) {}
						}

						if (font)
							break;
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.get_font(family, size);
			}

			case xivres::fontgen::game_font_family::ChnAXIS: {
				auto& font = s_fontSetCache[xivres::font_type::chn_axis];
				if (!font) {
					for (const auto pathList : { g_config.China }) {
						for (const auto& path : pathList) {
							try {
								font = xivres::installation(path).get_fontdata_set(xivres::font_type::chn_axis);
							} catch (...) {}
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.get_font(family, size);
			}

			case xivres::fontgen::game_font_family::KrnAXIS: {
				auto& font = s_fontSetCache[xivres::font_type::krn_axis];
				if (!font) {
					for (const auto pathList : { g_config.Korea }) {
						for (const auto& path : pathList) {
							try {
								font = xivres::installation(path).get_fontdata_set(xivres::font_type::krn_axis);
							} catch (...) {}
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.get_font(family, size);
			}

			case xivres::fontgen::game_font_family::tcaxis: {
				auto& font = s_fontSetCache[xivres::font_type::tc_axis];
				if (!font) {
					for (const auto pathList : { g_config.TraditionalChinese }) {
						for (const auto& path : pathList) {
							try {
								font = xivres::installation(path).get_fontdata_set(xivres::font_type::tc_axis);
							} catch (...) {}
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.get_font(family, size);
			}
		}
	} catch (const WException& e) {
		if (s_gameNotFoundDialogsEnabled && !s_showedGameNotFoundError) {
			s_showedGameNotFoundError = true;
			ShowErrorMessageBox(nullptr, IDS_ERROR_GAMENOTFOUND_BODY, e);
		}
	} catch (const std::system_error& e) {
		if (s_gameNotFoundDialogsEnabled && !s_showedGameNotFoundError) {
			s_showedGameNotFoundError = true;
			ShowErrorMessageBox(nullptr, IDS_ERROR_GAMENOTFOUND_BODY, e);
		}
	} catch (const std::exception& e) {
		if (s_gameNotFoundDialogsEnabled && !s_showedGameNotFoundError) {
			s_showedGameNotFoundError = true;
			ShowErrorMessageBox(nullptr, IDS_ERROR_GAMENOTFOUND_BODY, e);
		}
	}

	return std::make_shared<xivres::fontgen::empty_fixed_size_font>(size, xivres::fontgen::empty_fixed_size_font::create_struct{});
}

std::wstring App::Structs::LookupStruct::GetWeightString() const {
	switch (Weight) {
		case DWRITE_FONT_WEIGHT_THIN: return std::wstring(GetStringResource(IDS_FONTWEIGHT_100));
		case DWRITE_FONT_WEIGHT_EXTRA_LIGHT: return std::wstring(GetStringResource(IDS_FONTWEIGHT_200));
		case DWRITE_FONT_WEIGHT_LIGHT: return std::wstring(GetStringResource(IDS_FONTWEIGHT_300));
		case DWRITE_FONT_WEIGHT_SEMI_LIGHT: return std::wstring(GetStringResource(IDS_FONTWEIGHT_350));
		case DWRITE_FONT_WEIGHT_NORMAL: return std::wstring(GetStringResource(IDS_FONTWEIGHT_400));
		case DWRITE_FONT_WEIGHT_MEDIUM: return std::wstring(GetStringResource(IDS_FONTWEIGHT_500));
		case DWRITE_FONT_WEIGHT_SEMI_BOLD: return std::wstring(GetStringResource(IDS_FONTWEIGHT_600));
		case DWRITE_FONT_WEIGHT_BOLD: return std::wstring(GetStringResource(IDS_FONTWEIGHT_700));
		case DWRITE_FONT_WEIGHT_EXTRA_BOLD: return std::wstring(GetStringResource(IDS_FONTWEIGHT_800));
		case DWRITE_FONT_WEIGHT_BLACK: return std::wstring(GetStringResource(IDS_FONTWEIGHT_900));
		case DWRITE_FONT_WEIGHT_EXTRA_BLACK: return std::wstring(GetStringResource(IDS_FONTWEIGHT_950));
		default: return std::format(L"{}", static_cast<int>(Weight));
	}
}

std::wstring App::Structs::LookupStruct::GetStretchString() const {
	switch (Stretch) {
		case DWRITE_FONT_STRETCH_UNDEFINED: return L"-";
		case DWRITE_FONT_STRETCH_ULTRA_CONDENSED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_ULTRA_CONDENSED));
		case DWRITE_FONT_STRETCH_EXTRA_CONDENSED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_EXTRA_CONDENSED));
		case DWRITE_FONT_STRETCH_CONDENSED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_CONDENSED));
		case DWRITE_FONT_STRETCH_SEMI_CONDENSED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_SEMI_CONDENSED));
		case DWRITE_FONT_STRETCH_NORMAL: return std::wstring(GetStringResource(IDS_FONTSTRETCH_NORMAL));
		case DWRITE_FONT_STRETCH_SEMI_EXPANDED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_SEMI_EXPANDED));
		case DWRITE_FONT_STRETCH_EXPANDED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_EXPANDED));
		case DWRITE_FONT_STRETCH_EXTRA_EXPANDED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_EXTRA_EXPANDED));
		case DWRITE_FONT_STRETCH_ULTRA_EXPANDED: return std::wstring(GetStringResource(IDS_FONTSTRETCH_ULTRA_EXPANDED));
		default: return L"(?)";
	}
}

std::wstring App::Structs::LookupStruct::GetStyleString() const {
	switch (Style) {
		case DWRITE_FONT_STYLE_NORMAL: return std::wstring(GetStringResource(IDS_FONTSTYLE_NORMAL));
		case DWRITE_FONT_STYLE_OBLIQUE: return std::wstring(GetStringResource(IDS_FONTSTYLE_OBLIQUE));
		case DWRITE_FONT_STYLE_ITALIC: return std::wstring(GetStringResource(IDS_FONTSTYLE_ITALIC));
		default: return L"(?)";
	}
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

std::pair<IDWriteFactoryPtr, IDWriteFontPtr> App::Structs::LookupStruct::ResolveFont() const {
	using namespace xivres::fontgen;

	IDWriteFactoryPtr factory;
	// Fonts of the shared factory share their faces, and with them what DirectWrite keeps of the glyphs that it drew, so
	// that the bounds and pixels of a glyph could differ by a pixel depending on what other fonts of the same face drew
	// before, on any thread; exports then differed from run to run. Each font gets a factory of its own instead.
	SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)));

	IDWriteFontCollectionPtr coll;
	SuccessOrThrow(factory->GetSystemFontCollection(&coll));

	uint32_t index;
	BOOL exists;
	SuccessOrThrow(coll->FindFamilyName(xivres::util::unicode::convert<std::wstring>(Name).c_str(), &index, &exists));
	if (!exists)
		throw std::invalid_argument("Font not found");

	IDWriteFontFamilyPtr family;
	SuccessOrThrow(coll->GetFontFamily(index, &family));

	IDWriteFontPtr font;
	SuccessOrThrow(family->GetFirstMatchingFont(Weight, Stretch, Style, &font));

	// DirectWrite may match a simulated font, which is left to synthesis; take the real face that it is made from.
	if (Synthesis)
		font = GetRealFont(factory, font);

	return std::make_pair(std::move(factory), std::move(font));
}

std::map<uint32_t, float> App::Structs::LookupStruct::GetVariationAxisValues() const {
	std::map<uint32_t, float> res;
	for (const auto& [tag, value] : Variations) {
		if (tag.size() != 4)
			continue;
		uint32_t axisTag;
		memcpy(&axisTag, tag.data(), sizeof axisTag);
		res.emplace(axisTag, value);
	}
	return res;
}

static std::tuple<std::shared_ptr<xivres::stream>, int, std::map<uint32_t, float>> ReadFontStream(IDWriteFont* font) {
	using namespace xivres::fontgen;

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

	auto res = std::make_shared<xivres::memory_stream>();
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

	return { std::make_shared<xivres::memory_stream>(std::move(buf)), face->GetIndex(), std::move(instanceAxisValues) };
}

std::tuple<std::shared_ptr<xivres::stream>, int, std::map<uint32_t, float>> App::Structs::LookupStruct::ResolveStream() const {
	return ReadFontStream(ResolveFont().second);
}

float App::Structs::GetStretchPercent(DWRITE_FONT_STRETCH stretch) {
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

xivres::fontgen::font_render_transformation_matrix App::Structs::SynthesizedFace::GetScreenMatrix() const {
	// x' = ScaleX (x - slope y), where y grows downwards: points above the baseline move to the right.
	const auto slope = Oblique ? ObliqueSlope : 0.f;
	return {ScaleX, -slope * ScaleX, 0.f, 1.f};
}

float App::Structs::SynthesizedFace::GetEmbolden() const {
	// The bold simulation of DirectWrite makes Arial, Segoe UI, and Times New Roman 1/50 em wider and taller, and
	// advance 1/50 em further, as measured from GetGlyphRunOutline and GetDesignGlyphMetrics (41/2048 em wider and
	// 40/2048 em taller); going from 400 to 700 is taken to be that, and other weights in proportion.
	return static_cast<float>(WeightDelta) / 300.f / 50.f;
}

App::Structs::SynthesizedFace App::Structs::LookupStruct::ResolveSynthesis(RendererEnum renderer, IDWriteFont* font) const {
	SynthesizedFace res;
	if (!Synthesis || !font)
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

			const auto userAxes = GetVariationAxisValues();
			for (const auto& range : ranges) {
				const auto tag = static_cast<uint32_t>(range.axisTag);
				const auto userSet = userAxes.contains(tag);
				switch (range.axisTag) {
					case DWRITE_FONT_AXIS_TAG_WEIGHT:
						if (userSet) {
							realWeight = static_cast<int>(Weight);
						} else {
							const auto value = std::clamp(static_cast<float>(Weight), range.minValue, range.maxValue);
							res.AxisValues[tag] = value;
							realWeight = static_cast<int>(std::lround(value));
						}
						break;

					case DWRITE_FONT_AXIS_TAG_WIDTH:
						if (userSet) {
							realStretchPercent = GetStretchPercent(Stretch);
						} else if (const auto requested = GetStretchPercent(Stretch); requested > 0) {
							const auto value = std::clamp(requested, range.minValue, range.maxValue);
							res.AxisValues[tag] = value;
							realStretchPercent = value;
						}
						break;

					case DWRITE_FONT_AXIS_TAG_ITALIC:
						if (userSet) {
							realStyle = Style;
						} else if (Style != DWRITE_FONT_STYLE_NORMAL && range.maxValue >= 1.f) {
							res.AxisValues[tag] = 1.f;
							realStyle = DWRITE_FONT_STYLE_ITALIC;
						}
						break;

					case DWRITE_FONT_AXIS_TAG_SLANT:
						// Negative values lean to the right; as far as the slant of the oblique simulation, if the font goes that far.
						if (userSet) {
							realStyle = Style;
						} else if (Style != DWRITE_FONT_STYLE_NORMAL && range.minValue < 0.f && realStyle == DWRITE_FONT_STYLE_NORMAL) {
							constexpr auto RadiansToDegrees = 180.f / std::numbers::pi_v<float>;
							res.AxisValues[tag] = (std::max)(range.minValue, -std::atan(SynthesizedFace::ObliqueSlope) * RadiansToDegrees);
							realStyle = DWRITE_FONT_STYLE_OBLIQUE;
						}
						break;
				}
			}
		}
	}

	if (Synthesis->Allow) {
		// DirectWrite can only make a bold face, and only from faces that are not bold already.
		if (renderer == RendererEnum::FreeType)
			res.WeightDelta = static_cast<int>(Weight) - realWeight;
		else if (Weight >= DWRITE_FONT_WEIGHT_SEMI_BOLD && realWeight <= DWRITE_FONT_WEIGHT_MEDIUM)
			simulations = static_cast<DWRITE_FONT_SIMULATIONS>(simulations | DWRITE_FONT_SIMULATIONS_BOLD);

		if (Style != DWRITE_FONT_STYLE_NORMAL && realStyle == DWRITE_FONT_STYLE_NORMAL) {
			if (renderer == RendererEnum::FreeType)
				res.Oblique = true;
			else
				simulations = static_cast<DWRITE_FONT_SIMULATIONS>(simulations | DWRITE_FONT_SIMULATIONS_OBLIQUE);
		}

		if (const auto requested = GetStretchPercent(Stretch); requested > 0 && realStretchPercent > 0 && requested != realStretchPercent)  // NOLINT(clang-diagnostic-float-equal)
			res.ScaleX = requested / realStretchPercent;
	}

	res.Simulations = simulations;
	return res;
}

void App::Structs::LookupStruct::ConvertToExplicitSynthesis(RendererEnum renderer) {
	if (Synthesis || (renderer != RendererEnum::DirectWrite && renderer != RendererEnum::FreeType))
		return;

	try {
		const auto [factory, font] = ResolveFont();
		const auto simulations = font->GetSimulations();
		const auto realFont = GetRealFont(factory, font);

		// Ask for the real face, and for DirectWrite, for what makes the same simulations; FreeType did not simulate.
		Weight = realFont->GetWeight();
		Style = realFont->GetStyle();
		Stretch = realFont->GetStretch();
		if (renderer == RendererEnum::DirectWrite) {
			if (simulations & DWRITE_FONT_SIMULATIONS_BOLD)
				Weight = DWRITE_FONT_WEIGHT_BOLD;
			if ((simulations & DWRITE_FONT_SIMULATIONS_OBLIQUE) && Style == DWRITE_FONT_STYLE_NORMAL)
				Style = DWRITE_FONT_STYLE_ITALIC;
		}
	} catch (...) {
		// The font is not installed; keep the requested properties.
	}

	Synthesis = SynthesisStruct{};
}

namespace {
	using xivres::fontgen::font_render_transformation_matrix;

	// Returns a * b, with matrices laid out as [[M11, M12], [M21, M22]].
	font_render_transformation_matrix MultiplyMatrix(const font_render_transformation_matrix& a, const font_render_transformation_matrix& b) {
		return {
			a.M11 * b.M11 + a.M12 * b.M21,
			a.M11 * b.M12 + a.M12 * b.M22,
			a.M21 * b.M11 + a.M22 * b.M21,
			a.M21 * b.M12 + a.M22 * b.M22,
		};
	}

	std::shared_ptr<xivres::fontgen::fixed_size_font> CreateRendererFont(
		App::Structs::RendererEnum renderer,
		const App::Structs::LookupStruct& lookup,
		const App::Structs::RendererSpecificStruct& rendererSpecific,
		float size,
		float gamma,
		const font_render_transformation_matrix& matrix) {
		auto [factory, font] = lookup.ResolveFont();

		// The synthesis is part of the face: the glyphs are slanted and scaled by it before the given transformation.
		const auto synthesis = lookup.ResolveSynthesis(renderer, font);
		auto rendererMatrix = matrix;
		if (const auto synthesisMatrix = synthesis.GetScreenMatrix();
			synthesisMatrix.M11 != 1.f || synthesisMatrix.M12 != 0.f || synthesisMatrix.M21 != 0.f || synthesisMatrix.M22 != 1.f) {  // NOLINT(clang-diagnostic-float-equal)
			const auto screen = App::Structs::TransformStruct::RendererToScreen(renderer, matrix);
			rendererMatrix = App::Structs::TransformStruct::ScreenToRenderer(renderer, MultiplyMatrix(screen, synthesisMatrix));
		}

		switch (renderer) {
			case App::Structs::RendererEnum::DirectWrite: {
				auto specifics = rendererSpecific.DirectWrite;
				specifics.Features.clear();
				for (const auto& [tag, value] : lookup.Features)
					specifics.Features.push_back({ .nameTag = tag, .parameter = value });
				specifics.Language = lookup.Language;
				specifics.Variations = synthesis.AxisValues;
				for (const auto& [tag, value] : lookup.GetVariationAxisValues())
					specifics.Variations[tag] = value;
				specifics.Simulations = synthesis.Simulations;
				return std::make_shared<xivres::fontgen::directwrite_fixed_size_font>(std::move(factory), std::move(font), size, gamma, rendererMatrix, specifics);
			}

			case App::Structs::RendererEnum::FreeType: {
				auto [pStream, index, instanceAxisValues] = ReadFontStream(font);
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
				return std::make_shared<xivres::fontgen::freetype_fixed_size_font>(*pStream, index, size, gamma, rendererMatrix, specifics);
			}

			default:
				throw std::invalid_argument("Renderer does not draw from font files");
		}
	}

	// Returns the matrix for the renderer that transforms the texts of glyph merging: by the element's transformation,
	// then the text's, and then by the condensing.
	font_render_transformation_matrix ComposeTextMatrix(
		App::Structs::RendererEnum renderer,
		const App::Structs::TransformStruct& element,
		const App::Structs::TransformStruct& text,
		float condense) {
		const font_render_transformation_matrix condensing{condense, 0.f, 0.f, 1.f};
		const auto screen = MultiplyMatrix(condensing, MultiplyMatrix(text.GetScreenMatrix(renderer), element.GetScreenMatrix(renderer)));
		return App::Structs::TransformStruct::ScreenToRenderer(renderer, screen);
	}
}

App::Structs::TransformStruct::matrix App::Structs::TransformStruct::GetScreenMatrix(RendererEnum renderer) const {
	if (LegacyMatrix)
		return RendererToScreen(renderer, *LegacyMatrix);

	constexpr auto DegreesToRadians = std::numbers::pi_v<float> / 180.f;
	const auto c = std::cos(RotationDegrees * DegreesToRadians);
	const auto s = std::sin(RotationDegrees * DegreesToRadians);
	const auto t = std::tan(SkewDegrees * DegreesToRadians);

	// Counterclockwise on screen, where y grows downwards.
	const matrix rotation{c, s, -s, c};

	// x' = x - t y: points above the baseline move to the right.
	const matrix skew{1.f, -t, 0.f, 1.f};
	const matrix scale{ScaleX, 0.f, 0.f, ScaleY};
	return MultiplyMatrix(rotation, MultiplyMatrix(skew, scale));
}

App::Structs::TransformStruct::matrix App::Structs::TransformStruct::GetRendererMatrix(RendererEnum renderer) const {
	if (LegacyMatrix)
		return *LegacyMatrix;
	return ScreenToRenderer(renderer, GetScreenMatrix(renderer));
}

App::Structs::TransformStruct::matrix App::Structs::TransformStruct::ScreenToRenderer(RendererEnum renderer, const matrix& screen) {
	// DirectWrite transforms row vectors on screen: x' = x m11 + y m21.
	if (renderer == RendererEnum::DirectWrite)
		return {screen.M11, screen.M21, screen.M12, screen.M22};

	// Glyph images take the transformation on screen.
	if (renderer == RendererEnum::GlyphImages)
		return screen;

	// FreeType transforms column vectors in a space where y grows upwards.
	return {screen.M11, -screen.M12, -screen.M21, screen.M22};
}

App::Structs::TransformStruct::matrix App::Structs::TransformStruct::RendererToScreen(RendererEnum renderer, const matrix& m) {
	if (renderer == RendererEnum::DirectWrite)
		return {m.M11, m.M21, m.M12, m.M22};
	if (renderer == RendererEnum::GlyphImages)
		return m;
	return {m.M11, -m.M12, -m.M21, m.M22};
}

App::Structs::TransformStruct App::Structs::TransformStruct::FromScreenMatrix(const matrix& screen) {
	constexpr auto RadiansToDegrees = 180.f / std::numbers::pi_v<float>;
	TransformStruct res;
	res.ScaleX = std::hypot(screen.M11, screen.M21);
	float c = 1.f, s = 0.f;
	if (res.ScaleX > 1e-6f) {
		c = screen.M11 / res.ScaleX;
		s = -screen.M21 / res.ScaleX;
		res.RotationDegrees = std::atan2(s, c) * RadiansToDegrees;
	}

	// The remaining upper triangular part is skew * scale: [[ScaleX, -tan(skew) ScaleY], [0, ScaleY]].
	const auto u12 = c * screen.M12 - s * screen.M22;
	res.ScaleY = s * screen.M12 + c * screen.M22;
	if (std::abs(res.ScaleY) > 1e-6f)
		res.SkewDegrees = std::atan(-u12 / res.ScaleY) * RadiansToDegrees;
	return res;
}

bool App::Structs::TransformStruct::IsIdentity() const {
	if (LegacyMatrix)
		return LegacyMatrix->M11 == 1.f && LegacyMatrix->M12 == 0.f && LegacyMatrix->M21 == 0.f && LegacyMatrix->M22 == 1.f;  // NOLINT(clang-diagnostic-float-equal)
	return ScaleX == 1.f && ScaleY == 1.f && SkewDegrees == 0.f && RotationDegrees == 0.f;  // NOLINT(clang-diagnostic-float-equal)
}

void App::Structs::to_json(nlohmann::json& json, const TransformStruct& value) {
	json = nlohmann::json::object({
		{"scaleX", value.ScaleX},
		{"scaleY", value.ScaleY},
		{"skew", value.SkewDegrees},
		{"rotation", value.RotationDegrees},
	});
}

void App::Structs::from_json(const nlohmann::json& json, TransformStruct& value) {
	value = {};
	if (!json.is_object())
		return;
	value.ScaleX = json.value<float>("scaleX", 1.f);
	value.ScaleY = json.value<float>("scaleY", 1.f);
	value.SkewDegrees = json.value<float>("skew", 0.f);
	value.RotationDegrees = json.value<float>("rotation", 0.f);
}

const std::shared_ptr<xivres::fontgen::fixed_size_font>& App::Structs::FaceElement::GetBaseFont() const {
	if (!m_baseFont) {
		try {
			switch (Renderer) {
				case RendererEnum::Empty:
					m_baseFont = std::make_shared<xivres::fontgen::empty_fixed_size_font>(Size, RendererSpecific.Empty);
					break;

				case RendererEnum::PrerenderedGameInstallation:
					if (Lookup.Name == "AXIS")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::AXIS, Size);
					else if (Lookup.Name == "Jupiter")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::Jupiter, Size);
					else if (Lookup.Name == "JupiterN")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::JupiterN, Size);
					else if (Lookup.Name == "Meidinger")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::Meidinger, Size);
					else if (Lookup.Name == "MiedingerMid")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::MiedingerMid, Size);
					else if (Lookup.Name == "TrumpGothic")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::TrumpGothic, Size);
					else if (Lookup.Name == "ChnAXIS")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::ChnAXIS, Size);
					else if (Lookup.Name == "KrnAXIS")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::KrnAXIS, Size);
					else if (Lookup.Name == "tcaxis")
						m_baseFont = GetGameFont(xivres::fontgen::game_font_family::tcaxis, Size);
					else
						throw std::runtime_error("Invalid name");
					break;

				case RendererEnum::DirectWrite:
				case RendererEnum::FreeType: {
					auto font = CreateRendererFont(Renderer, Lookup, RendererSpecific, Size, Gamma, Transform.GetRendererMatrix(Renderer));
					if (!GlyphMerging.IsEnabled()) {
						m_baseFont = std::move(font);
						break;
					}

					m_baseFont = std::make_shared<xivres::fontgen::glyph_merging_fixed_size_font>(
						std::move(font),
						GlyphMerging.Params,
						[renderer = Renderer, lookup = Lookup, rendererSpecific = RendererSpecific, gamma = Gamma, elementTransform = Transform, textTransform = GlyphMerging.TextTransform](float size, float condense) {
							return CreateRendererFont(renderer, lookup, rendererSpecific, size, gamma, ComposeTextMatrix(renderer, elementTransform, textTransform, condense));
						});
					break;
				}

				case RendererEnum::GlyphImages: {
					auto font = GlyphFiles::CreateFont(RendererSpecific.GlyphImages, Size, Gamma, Transform.GetRendererMatrix(Renderer));
					if (!GlyphMerging.IsEnabled()) {
						m_baseFont = std::move(font);
						break;
					}

					// Texts are drawn with the font that the lookup names, or with the glyph files themselves if it names none.
					m_baseFont = std::make_shared<xivres::fontgen::glyph_merging_fixed_size_font>(
						std::move(font),
						GlyphMerging.Params,
						[lookup = Lookup, rendererSpecific = RendererSpecific, gamma = Gamma, elementTransform = Transform, textTransform = GlyphMerging.TextTransform](float size, float condense) -> std::shared_ptr<xivres::fontgen::fixed_size_font> {
							if (!lookup.Name.empty()) {
								try {
									return CreateRendererFont(RendererEnum::DirectWrite, lookup, rendererSpecific, size, gamma, ComposeTextMatrix(RendererEnum::DirectWrite, elementTransform, textTransform, condense));
								} catch (...) {
									// fall back to the glyph files
								}
							}
							return GlyphFiles::CreateFont(rendererSpecific.GlyphImages, size, gamma, ComposeTextMatrix(RendererEnum::GlyphImages, elementTransform, textTransform, condense));
						});
					break;
				}

				default:
					m_baseFont = std::make_shared<xivres::fontgen::empty_fixed_size_font>();
					break;
			}
		} catch (...) {
			m_baseFont = std::make_shared<xivres::fontgen::empty_fixed_size_font>(Size, RendererSpecific.Empty);
		}
	}

	return m_baseFont;
}


const std::shared_ptr<xivres::fontgen::wrapping_fixed_size_font>& App::Structs::FaceElement::GetWrappedFont() const {
	if (!m_wrappedFont) {
		// Glyphs squeezed by monospacing are rendered narrower by the renderer, which keeps their hinting and stroke weight.
		// Fonts of glyph merging have glyphs that the renderer alone cannot make, so their glyphs are resampled instead.
		xivres::fontgen::wrapping_fixed_size_font::scaled_font_factory scaledFontFactory;
		if ((Renderer == RendererEnum::DirectWrite || Renderer == RendererEnum::FreeType) && !GlyphMerging.IsEnabled() && WrapModifiers.Monospacing.is_enabled()) {
			scaledFontFactory = [renderer = Renderer, lookup = Lookup, rendererSpecific = RendererSpecific, size = Size, gamma = Gamma, transform = Transform](float scaleX) {
				const font_render_transformation_matrix scaling{scaleX, 0.f, 0.f, 1.f};
				const auto screen = MultiplyMatrix(scaling, transform.GetScreenMatrix(renderer));
				return CreateRendererFont(renderer, lookup, rendererSpecific, size, gamma, TransformStruct::ScreenToRenderer(renderer, screen));
			};
		}
		m_wrappedFont = std::make_shared<xivres::fontgen::wrapping_fixed_size_font>(GetBaseFont(), WrapModifiers, std::move(scaledFontFactory));
	}

	return m_wrappedFont;
}

void App::Structs::FaceElement::FlushCache() {
	m_baseFont.reset();
	m_wrappedFont.reset();
}

void App::Structs::FaceElement::OnFontWrappingParametersChange() {
	m_wrappedFont = nullptr;
}

void App::Structs::FaceElement::OnFontCreateParametersChange() {
	m_wrappedFont = nullptr;
	m_baseFont = nullptr;
}

void App::Structs::FaceElement::Scale(float factor) {
	// Nothing is rounded here; the fonts round the values in pixels when they use them, so that scaling back and forth
	// keeps the values.
	Size *= factor;

	RendererSpecific.Empty.Ascent *= factor;
	RendererSpecific.Empty.LineHeight *= factor;

	WrapModifiers.LetterSpacing *= factor;
	WrapModifiers.HorizontalOffset *= factor;
	WrapModifiers.BaselineShift *= factor;
	if (auto& monospacing = WrapModifiers.Monospacing; monospacing.Unit == xivres::fontgen::monospacing_unit::Pixels) {
		if (monospacing.MinAdvance)
			*monospacing.MinAdvance *= factor;
		if (monospacing.MaxAdvance)
			*monospacing.MaxAdvance *= factor;
	}

	auto& glyphMerge = GlyphMerging.Params;
	if (glyphMerge.TextSize)
		*glyphMerge.TextSize *= factor;
	glyphMerge.TextOffsetX *= factor;
	glyphMerge.TextOffsetY *= factor;
	glyphMerge.LetterSpacing *= factor;
	glyphMerge.LineSpacing *= factor;

	OnFontCreateParametersChange();
}

// Returns what of the lookup decides the font that it makes, other than the name, weight, stretch, and style.
static std::string GetLookupKeySuffix(const App::Structs::LookupStruct& lookup) {
	std::string res;
	for (const auto& [tag, value] : lookup.Features)
		res += std::format(":{}={}", std::string_view(reinterpret_cast<const char*>(&tag), 4), value);
	res += std::format(":lang={}", lookup.Language);
	if (lookup.Synthesis)
		res += std::format(":synth={}", lookup.Synthesis->Allow ? 1 : 0);
	for (const auto& [tag, value] : lookup.Variations)
		res += std::format(":{}={:g}", tag, value);
	return res;
}

std::string App::Structs::FaceElement::GetBaseFontKey() const {
	switch (Renderer) {
		case RendererEnum::Empty:
			return std::format("empty:{:g}:{:g}:{:g}", Size, RendererSpecific.Empty.Ascent, RendererSpecific.Empty.LineHeight);
		case RendererEnum::PrerenderedGameInstallation:
			return std::format("game:{}:{:g}", Lookup.Name, Size);
		case RendererEnum::DirectWrite: {
			const auto matrix = Transform.GetRendererMatrix(Renderer);
			auto res = std::format("directwrite:{}:{:g}:{:g}:{}:{}:{}:{}:{}:{}:{:08X}{:08X}{:08X}{:08X}",
				Lookup.Name,
				Size,
				Gamma,
				static_cast<uint32_t>(Lookup.Weight),
				static_cast<uint32_t>(Lookup.Stretch),
				static_cast<uint32_t>(Lookup.Style),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.RenderMode),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.MeasureMode),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.GridFitMode),
				std::bit_cast<uint32_t>(matrix.M11),
				std::bit_cast<uint32_t>(matrix.M12),
				std::bit_cast<uint32_t>(matrix.M21),
				std::bit_cast<uint32_t>(matrix.M22)
			);
			res += GetLookupKeySuffix(Lookup);
			if (GlyphMerging.IsEnabled())
				res += ":merge=" + nlohmann::json(GlyphMerging).dump();
			return res;
		}
		case RendererEnum::FreeType: {
			const auto matrix = Transform.GetRendererMatrix(Renderer);
			auto res = std::format("freetype:{}:{:g}:{:g}:{}:{}:{}:{}:{}:{:08X}{:08X}{:08X}{:08X}",
				Lookup.Name,
				Size,
				Gamma,
				static_cast<uint32_t>(Lookup.Weight),
				static_cast<uint32_t>(Lookup.Stretch),
				static_cast<uint32_t>(Lookup.Style),
				static_cast<uint32_t>(RendererSpecific.FreeType.LoadFlags),
				static_cast<uint32_t>(RendererSpecific.FreeType.RenderMode),
				std::bit_cast<uint32_t>(matrix.M11),
				std::bit_cast<uint32_t>(matrix.M12),
				std::bit_cast<uint32_t>(matrix.M21),
				std::bit_cast<uint32_t>(matrix.M22)
			);
			res += GetLookupKeySuffix(Lookup);
			if (GlyphMerging.IsEnabled())
				res += ":merge=" + nlohmann::json(GlyphMerging).dump();
			return res;
		}
		case RendererEnum::GlyphImages: {
			const auto& glyphImages = RendererSpecific.GlyphImages;
			const auto matrix = Transform.GetRendererMatrix(Renderer);
			auto settings = nlohmann::json(RendererSpecific)["glyphImages"];
			auto res = std::format("glyphimages:{}:{:g}:{:g}:{:08X}{:08X}{:08X}{:08X}:{:016X}",
				xivres::util::unicode::convert<std::string>(GlyphFiles::ResolvePath(glyphImages.Path).wstring()),
				Size,
				Gamma,
				std::bit_cast<uint32_t>(matrix.M11),
				std::bit_cast<uint32_t>(matrix.M12),
				std::bit_cast<uint32_t>(matrix.M21),
				std::bit_cast<uint32_t>(matrix.M22),
				std::hash<std::string>{}(settings.dump()));
			if (GlyphMerging.IsEnabled()) {
				// The texts are drawn by DirectWrite with the font of the lookup, if it names one.
				res += ":merge=" + nlohmann::json(GlyphMerging).dump();
				if (!Lookup.Name.empty()) {
					res += std::format(":{}:{}:{}:{}:{}:{}:{}",
						Lookup.Name,
						static_cast<uint32_t>(Lookup.Weight),
						static_cast<uint32_t>(Lookup.Stretch),
						static_cast<uint32_t>(Lookup.Style),
						static_cast<uint32_t>(RendererSpecific.DirectWrite.RenderMode),
						static_cast<uint32_t>(RendererSpecific.DirectWrite.MeasureMode),
						static_cast<uint32_t>(RendererSpecific.DirectWrite.GridFitMode));
					res += GetLookupKeySuffix(Lookup);
				}
			}
			return res;
		}
		default:
			throw std::runtime_error("Invalid renderer");
	}
}

bool App::Structs::FaceElement::UsesProjectDirectory() const {
	const auto& glyphImages = RendererSpecific.GlyphImages;
	return Renderer == RendererEnum::GlyphImages
		&& !glyphImages.IsEmbedded()
		&& std::filesystem::path(xivres::util::unicode::convert<std::wstring>(glyphImages.Path)).is_relative();
}

std::wstring App::Structs::FaceElement::GetRangeRepresentation() const {
	if (WrapModifiers.Codepoints.empty())
		return L"(None)";

	std::wstring res;
	std::vector<char32_t> charVec(GetBaseFont()->all_codepoints().begin(), GetBaseFont()->all_codepoints().end());
	for (const auto& [c1, c2] : WrapModifiers.Codepoints) {
		if (!res.empty())
			res += L", ";

		const auto left = std::ranges::lower_bound(charVec, c1);
		const auto right = std::ranges::upper_bound(charVec, c2);
		const auto count = right - left;

		const auto blk = std::lower_bound(xivres::util::unicode::blocks::all_blocks().begin(), xivres::util::unicode::blocks::all_blocks().end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
		if (blk != xivres::util::unicode::blocks::all_blocks().end() && blk->First == c1 && blk->Last == c2) {
			res += std::format(L"{}({})", xivres::util::unicode::convert<std::wstring>(blk->Name), count);
		} else if (c1 == c2) {
			res += std::format(
				L"U+{:04X} [{}]",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			);
		} else {
			res += std::format(
				L"U+{:04X}~{:04X} ({}) {} ~ {}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				count,
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			);
		}
	}

	return res;
}

std::wstring App::Structs::FaceElement::GetRendererRepresentation() const {
	switch (Renderer) {
		case RendererEnum::Empty:
			return L"Empty";

		case RendererEnum::PrerenderedGameInstallation:
			return L"Prerendered (Game)";

		case RendererEnum::DirectWrite:
			return std::format(L"DirectWrite ({}, {}, {})",
				RendererSpecific.DirectWrite.get_rendering_mode_string(),
				RendererSpecific.DirectWrite.get_measuring_mode_string(),
				RendererSpecific.DirectWrite.get_grid_fit_mode_string()
			);

		case RendererEnum::FreeType:
			return std::format(L"FreeType ({}, {})", RendererSpecific.FreeType.get_render_mode_string(), RendererSpecific.FreeType.get_load_flags_string());

		case RendererEnum::GlyphImages:
			return L"SVG/PNG";

		default:
			return L"INVALID";
	}
}

std::wstring App::Structs::FaceElement::GetLookupRepresentation() const {
	switch (Renderer) {
		case RendererEnum::DirectWrite:
		case RendererEnum::FreeType:
			return std::format(L"{} ({}, {}, {})",
				xivres::util::unicode::convert<std::wstring>(Lookup.Name),
				Lookup.GetWeightString(),
				Lookup.GetStyleString(),
				Lookup.GetStretchString()
			);

		case RendererEnum::GlyphImages:
			return RendererSpecific.GlyphImages.IsEmbedded()
				? std::format(L"(Embedded: {})", RendererSpecific.GlyphImages.Embedded.size())
				: xivres::util::unicode::convert<std::wstring>(RendererSpecific.GlyphImages.Path);

		default:
			return L"-";
	}
}

App::Structs::FaceElement::FaceElement() noexcept = default;

App::Structs::FaceElement::FaceElement(FaceElement&& r) noexcept : FaceElement() {
	swap(*this, r);
}

App::Structs::FaceElement::FaceElement(const FaceElement& r)
	: m_baseFont(r.m_baseFont)
	, m_wrappedFont(r.m_wrappedFont)
	, Size(r.Size)
	, Gamma(r.Gamma)
	, MergeMode(r.MergeMode)
	, Transform(r.Transform)
	, WrapModifiers(r.WrapModifiers)
	, Renderer(r.Renderer)
	, Lookup(r.Lookup)
	, RendererSpecific(r.RendererSpecific)
	, GlyphMerging(r.GlyphMerging) {
}

App::Structs::FaceElement App::Structs::FaceElement::operator=(FaceElement&& r) noexcept {
	swap(*this, r);
	return *this;
}

App::Structs::FaceElement App::Structs::FaceElement::operator=(const FaceElement& r) {
	auto r2(r);
	swap(*this, r2);
	return *this;
}

void App::Structs::swap(FaceElement& l, FaceElement& r) noexcept {
	if (&l == &r)
		return;

	using std::swap;
	swap(l.m_baseFont, r.m_baseFont);
	swap(l.m_wrappedFont, r.m_wrappedFont);
	swap(l.Size, r.Size);
	swap(l.Gamma, r.Gamma);
	swap(l.MergeMode, r.MergeMode);
	swap(l.Transform, r.Transform);
	swap(l.WrapModifiers, r.WrapModifiers);
	swap(l.Renderer, r.Renderer);
	swap(l.Lookup, r.Lookup);
	swap(l.RendererSpecific, r.RendererSpecific);
	swap(l.GlyphMerging, r.GlyphMerging);
}

const std::shared_ptr<xivres::fontgen::fixed_size_font>& App::Structs::Face::GetMergedFont() const {
	if (!MergedFont) {
		std::vector<std::pair<std::shared_ptr<xivres::fontgen::fixed_size_font>, xivres::fontgen::codepoint_merge_mode>> mergeFontList;

		for (auto& pElement : Elements)
			mergeFontList.emplace_back(pElement->GetWrappedFont(), pElement->MergeMode);

		MergedFont = std::make_shared<xivres::fontgen::merged_fixed_size_font>(std::move(mergeFontList), VerticalAlignment);
	}

	return MergedFont;
}

const std::shared_ptr<xivres::fontgen::fixed_size_font>& App::Structs::Face::GetPreviewMergedFont() const {
	// Elements that are gone may leave their addresses to new ones.
	std::erase_if(DeactivatedElements, [this](const FaceElement* p) {
		return std::ranges::none_of(Elements, [p](const auto& e) { return e.get() == p; });
	});
	if (DeactivatedElements.empty())
		return GetMergedFont();

	if (!PreviewMergedFont) {
		std::vector<std::pair<std::shared_ptr<xivres::fontgen::fixed_size_font>, xivres::fontgen::codepoint_merge_mode>> mergeFontList;

		for (auto& pElement : Elements) {
			if (!DeactivatedElements.contains(pElement.get()))
				mergeFontList.emplace_back(pElement->GetWrappedFont(), pElement->MergeMode);
		}

		PreviewMergedFont = std::make_shared<xivres::fontgen::merged_fixed_size_font>(std::move(mergeFontList), VerticalAlignment);
	}

	return PreviewMergedFont;
}

void App::Structs::Face::SetElementDeactivated(const FaceElement& element, bool deactivated) {
	if (deactivated ? DeactivatedElements.insert(&element).second : DeactivatedElements.erase(&element) > 0)
		PreviewMergedFont = nullptr;
}

void App::Structs::Face::FlushCache() {
	MergedFont.reset();
	PreviewMergedFont.reset();
	for (const auto& e : Elements)
		e->FlushCache();
}

void App::Structs::Face::OnElementChange() {
	MergedFont = nullptr;
	PreviewMergedFont = nullptr;
}

App::Structs::Face::Face() noexcept = default;

App::Structs::Face::Face(Face&& r) noexcept : Face() {
	swap(*this, r);
}

App::Structs::Face::Face(const Face& r)
	: MergedFont(r.MergedFont)
	, Name(r.Name)
	, PreviewText(r.PreviewText)
	, VerticalAlignment(r.VerticalAlignment) {
	Elements.reserve(r.Elements.size());
	for (const auto& e : r.Elements)
		Elements.emplace_back(std::make_unique<FaceElement>(*e));
}

App::Structs::Face& App::Structs::Face::operator=(Face&& r) noexcept {
	swap(*this, r);
	return *this;
}

App::Structs::Face& App::Structs::Face::operator=(const Face& r) {
	auto r2(r);
	swap(*this, r2);
	return *this;
}

void App::Structs::swap(Face& l, Face& r) noexcept {
	if (&l == &r)
		return;

	using std::swap;
	swap(l.Name, r.Name);
	swap(l.PreviewText, r.PreviewText);
	swap(l.Elements, r.Elements);
	swap(l.VerticalAlignment, r.VerticalAlignment);
	swap(l.DeactivatedElements, r.DeactivatedElements);
	swap(l.PreviewMergedFont, r.PreviewMergedFont);
}

void App::Structs::FontSet::FlushCache() {
	for (const auto& e : Faces)
		e->FlushCache();
}

void App::Structs::FontSet::ConsolidateFonts() const {
	std::map<std::string, std::shared_ptr<xivres::fontgen::fixed_size_font>> loadedBaseFonts;
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

App::Structs::FontSet App::Structs::FontSet::NewFromTemplateFont(xivres::font_type fontType) {
	FontSet res{};

	if (const auto pcszFmt = xivres::fontgen::get_font_tex_filename_format(fontType)) {
		std::string_view filename(pcszFmt);
		filename = filename.substr(filename.rfind('/') + 1);
		res.TexFilenameFormat = filename;
	} else
		res.TexFilenameFormat = "font{}.tex";

	for (const auto& def : xivres::fontgen::get_fontdata_definition(fontType)) {
		std::string_view filename(def.Path);
		filename = filename.substr(filename.rfind('/') + 1);
		filename = filename.substr(0, filename.find('.'));

		std::string previewText;
		std::vector<std::pair<char32_t, char32_t>> codepoints;
		if (filename == "Jupiter_45" || filename == "Jupiter_90") {
			previewText = "123,456,789.000!!!";
			codepoints = { {U'0', U'9'}, {U'!', U'!'}, {U'.', U'.'}, {U',', U','} };
		} else if (filename.starts_with("Meidinger_")) {
			previewText = "0123456789?!%+-./";
			codepoints = { {0x0000, 0x10FFFF} };
		} else {
			previewText = GetDefaultPreviewText();
			codepoints = { {0x0000, 0x10FFFF} };
		}

		auto& face = *res.Faces.emplace_back(std::make_unique<Face>());
		face.Name = std::string(filename);
		face.PreviewText = previewText;

		auto& element = *face.Elements.emplace_back(std::make_unique<FaceElement>());
		element.Size = def.Size;
		element.WrapModifiers = {
			.Codepoints = std::move(codepoints),
		};
		element.Renderer = RendererEnum::PrerenderedGameInstallation;
		element.Lookup = {
			.Name = def.Name,
		};
	}

	return res;
}

void App::Structs::MultiFontSet::FlushCache() {
	for (const auto& e : FontSets)
		e->FlushCache();
}

void App::Structs::SetGameNotFoundDialogsEnabled(bool enabled) {
	s_gameNotFoundDialogsEnabled = enabled;
}

void App::Structs::FlushCachedFonts() {
	const auto lock = std::scoped_lock(s_fontSetCacheMtx);
	s_fontSetCache.clear();
	s_showedGameNotFoundError = false;
}

static std::filesystem::path s_projectDirectory;
static std::mutex s_projectDirectoryMtx;

void App::Structs::SetProjectDirectory(std::filesystem::path path) {
	const auto lock = std::scoped_lock(s_projectDirectoryMtx);
	s_projectDirectory = std::move(path);
}

std::filesystem::path App::Structs::GetProjectDirectory() {
	const auto lock = std::scoped_lock(s_projectDirectoryMtx);
	return s_projectDirectory;
}

void App::Structs::OnProjectDirectoryChange(const MultiFontSet& multiFontSet) {
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

void App::Structs::from_json(const nlohmann::json& json, FontSet& value) {
	if (!json.is_object()) {
		value = {};
		return;
	}

	value.Faces.clear();
	if (const auto it = json.find("faces"); it != json.end() && it->is_array()) {
		for (const auto& v : *it)
			value.Faces.emplace_back(std::make_unique<Face>(v.get<Face>()));
	}
	value.DiscardStep = json.value<int>("discardStep", 1);
	value.SideLength = json.value<int>("sideLength", 4096);
	value.ExpectedTexCount = json.value<int>("expectedTexCount", 1);
	value.TexFilenameFormat = json.value<std::string>("texFilenameFormat", "");
}

void App::Structs::to_json(nlohmann::json& json, const FontSet& value) {
	json = nlohmann::json::object();
	auto& faces = *json.emplace("faces", nlohmann::json::array()).first;
	for (const auto& e : value.Faces)
		faces.emplace_back(*e);
	json.emplace("discardStep", value.DiscardStep);
	json.emplace("sideLength", value.SideLength);
	json.emplace("expectedTexCount", value.ExpectedTexCount);
	json.emplace("texFilenameFormat", value.TexFilenameFormat);
}

static constexpr std::pair<xivres::fontgen::vertical_alignment, std::string_view> VerticalAlignmentNames[]{
	{ xivres::fontgen::vertical_alignment::Top, "top" },
	{ xivres::fontgen::vertical_alignment::Middle, "middle" },
	{ xivres::fontgen::vertical_alignment::Baseline, "baseline" },
	{ xivres::fontgen::vertical_alignment::Bottom, "bottom" },
	{ xivres::fontgen::vertical_alignment::RomanBaseline, "romanBaseline" },
	{ xivres::fontgen::vertical_alignment::IdeographicCenter, "ideographicCenter" },
};

void App::Structs::from_json(const nlohmann::json& json, Face& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Name = json.value<std::string>("name", "");
	value.Elements.clear();
	if (const auto it = json.find("elements"); it != json.end() && it->is_array()) {
		for (const auto& v : *it)
			value.Elements.emplace_back(std::make_unique<FaceElement>(v.get<FaceElement>()));
	}
	value.PreviewText = json.value<std::string>("previewText", "");
	value.VerticalAlignment = xivres::fontgen::vertical_alignment::Baseline;
	const auto verticalAlignment = json.value<std::string>("verticalAlignment", "");
	for (const auto& [alignment, name] : VerticalAlignmentNames) {
		if (name == verticalAlignment)
			value.VerticalAlignment = alignment;
	}
}

void App::Structs::to_json(nlohmann::json& json, const Face& value) {
	json = nlohmann::json::object();
	json.emplace("name", value.Name);
	auto& elements = *json.emplace("elements", nlohmann::json::array()).first;
	for (const auto& e : value.Elements)
		elements.emplace_back(*e);
	json.emplace("previewText", value.PreviewText);
	for (const auto& [alignment, name] : VerticalAlignmentNames) {
		if (alignment == value.VerticalAlignment && value.VerticalAlignment != xivres::fontgen::vertical_alignment::Baseline)
			json.emplace("verticalAlignment", name);
	}
}

void App::Structs::from_json(const nlohmann::json& json, FaceElement& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Size = json.value<float>("size", 0.f);
	value.Gamma = json.value<float>("gamma", 1.f);
	if (const auto it = json.find("mergeMode"); it != json.end() && it->is_number_integer()) {
		switch (it->get<int>()) {
			case static_cast<int>(xivres::fontgen::codepoint_merge_mode::AddAll):
				value.MergeMode = xivres::fontgen::codepoint_merge_mode::AddAll;
				break;
			case static_cast<int>(xivres::fontgen::codepoint_merge_mode::AddNew):
				value.MergeMode = xivres::fontgen::codepoint_merge_mode::AddNew;
				break;
			case static_cast<int>(xivres::fontgen::codepoint_merge_mode::Replace):
				value.MergeMode = xivres::fontgen::codepoint_merge_mode::Replace;
				break;
		}
	} else if (const auto it = json.find("overwrite"); it != json.end() && it->is_boolean()) {
		if (it->get<bool>())
			value.MergeMode = xivres::fontgen::codepoint_merge_mode::AddAll;
		else
			value.MergeMode = xivres::fontgen::codepoint_merge_mode::AddNew;
	}
	if (const auto it = json.find("wrapModifiers"); it != json.end())
		from_json(*it, value.WrapModifiers);
	else
		value.WrapModifiers = {};
	value.Renderer = static_cast<RendererEnum>(json.value<int>("renderer", static_cast<int>(RendererEnum::Empty)));
	if (const auto it = json.find("lookup"); it != json.end())
		from_json(*it, value.Lookup);
	else
		value.Lookup = {};
	if (const auto it = json.find("renderSpecific"); it != json.end())
		from_json(*it, value.RendererSpecific);
	else
		value.RendererSpecific = {};

	value.Transform = {};
	if (const auto it = json.find("transform"); it != json.end()) {
		from_json(*it, value.Transform);
	} else if (const auto it2 = json.find("transformationMatrix"); it2 != json.end() && it2->is_array() && it2->size() == 4
		&& std::ranges::all_of(*it2, [](const nlohmann::json& v) { return v.is_number(); })) {
		// Stored by earlier versions; kept as is, unless it does nothing or is unusable, which is taken as identity.
		const TransformStruct::matrix m{it2->at(0).get<float>(), it2->at(1).get<float>(), it2->at(2).get<float>(), it2->at(3).get<float>()};
		const auto finite = std::isfinite(m.M11) && std::isfinite(m.M12) && std::isfinite(m.M21) && std::isfinite(m.M22);
		const auto invertible = finite && std::abs(m.M11 * m.M22 - m.M12 * m.M21) > 1e-6f;
		const auto identity = m.M11 == 1.f && m.M12 == 0.f && m.M21 == 0.f && m.M22 == 1.f;  // NOLINT(clang-diagnostic-float-equal)
		if (invertible && !identity)
			value.Transform.LegacyMatrix = m;
	}

	if (const auto it = json.find("glyphMerging"); it != json.end())
		from_json(*it, value.GlyphMerging);
	else
		value.GlyphMerging = {};
}

void App::Structs::to_json(nlohmann::json& json, const FaceElement& value) {
	json = nlohmann::json::object();
	json.emplace("size", FloatToJson(value.Size));
	json.emplace("gamma", value.Gamma);
	json.emplace("mergeMode", value.MergeMode);
	json.emplace("wrapModifiers", value.WrapModifiers);
	if (const auto& m = value.Transform.LegacyMatrix)
		json.emplace("transformationMatrix", nlohmann::json::array({m->M11, m->M12, m->M21, m->M22}));
	else
		json.emplace("transform", value.Transform);
	json.emplace("renderer", static_cast<int>(value.Renderer));
	json.emplace("lookup", value.Lookup);
	json.emplace("renderSpecific", value.RendererSpecific);
	if (value.GlyphMerging.IsEnabled())
		json.emplace("glyphMerging", value.GlyphMerging);
}

namespace {
	using xivres::fontgen::glyph_merge_fit_mode;
	using xivres::fontgen::glyph_merge_line_alignment;
	using xivres::fontgen::glyph_merge_shape;
	using xivres::fontgen::glyph_merge_text_mode;

	constexpr std::pair<glyph_merge_shape, const char*> GlyphMergeShapeNames[]{
		{glyph_merge_shape::None, "none"},
		{glyph_merge_shape::AmPm, "amPm"},
		{glyph_merge_shape::Ime, "ime"},
		{glyph_merge_shape::Box, "box"},
		{glyph_merge_shape::NumberBox, "numberBox"},
		{glyph_merge_shape::HollowBox, "hollowBox"},
		{glyph_merge_shape::Hexagon, "hexagon"},
		{glyph_merge_shape::Rhombus, "rhombus"},
		{glyph_merge_shape::Bozja, "bozja"},
		{glyph_merge_shape::Time, "time"},
		{glyph_merge_shape::Custom, "custom"},
		{glyph_merge_shape::Glyph, "glyph"},
	};

	constexpr std::pair<glyph_merge_text_mode, const char*> GlyphMergeTextModeNames[]{
		{glyph_merge_text_mode::Subtract, "subtract"},
		{glyph_merge_text_mode::Difference, "difference"},
	};

	constexpr std::pair<glyph_merge_fit_mode, const char*> GlyphMergeFitModeNames[]{
		{glyph_merge_fit_mode::CondenseThenShrink, "condenseThenShrink"},
		{glyph_merge_fit_mode::Shrink, "shrink"},
		{glyph_merge_fit_mode::Overflow, "overflow"},
	};

	constexpr std::pair<xivres::fontgen::image_coverage_mode, const char*> ImageCoverageModeNames[]{
		{xivres::fontgen::image_coverage_mode::Auto, "auto"},
		{xivres::fontgen::image_coverage_mode::Alpha, "alpha"},
		{xivres::fontgen::image_coverage_mode::Darkness, "darkness"},
		{xivres::fontgen::image_coverage_mode::Brightness, "brightness"},
	};

	constexpr std::pair<glyph_merge_line_alignment, const char*> GlyphMergeLineAlignmentNames[]{
		{glyph_merge_line_alignment::Left, "left"},
		{glyph_merge_line_alignment::Center, "center"},
		{glyph_merge_line_alignment::Right, "right"},
	};

	template<typename T, size_t N>
	const char* NameOf(const std::pair<T, const char*>(&names)[N], T value) {
		for (const auto& [v, name] : names) {
			if (v == value)
				return name;
		}
		return names[0].second;
	}

	template<typename T, size_t N>
	T ValueOf(const std::pair<T, const char*>(&names)[N], const nlohmann::json& json, const char* key, T defaultValue) {
		if (const auto it = json.find(key); it != json.end() && it->is_string()) {
			const auto& s = it->get_ref<const std::string&>();
			for (const auto& [v, name] : names) {
				if (s == name)
					return v;
			}
		}
		return defaultValue;
	}
}

void App::Structs::to_json(nlohmann::json& json, const GlyphMergingStruct& value) {
	const auto& params = value.Params;
	json = nlohmann::json::object();
	json.emplace("textSize", params.TextSize ? nlohmann::json(*params.TextSize) : nlohmann::json(nullptr));
	json.emplace("fitMode", NameOf(GlyphMergeFitModeNames, params.FitMode));
	json.emplace("textOffset", nlohmann::json::array({params.TextOffsetX, params.TextOffsetY}));
	json.emplace("textTransform", value.TextTransform);
	json.emplace("letterSpacing", params.LetterSpacing);
	json.emplace("lineSpacing", params.LineSpacing);
	json.emplace("lineAlignment", NameOf(GlyphMergeLineAlignmentNames, params.LineAlignment));

	auto mappings = nlohmann::json::array();
	for (const auto& mapping : params.Mappings) {
		auto texts = nlohmann::json::array();
		for (const auto& text : mapping.Texts)
			texts.emplace_back(xivres::util::unicode::convert<std::string>(text));

		auto m = nlohmann::json::object();
		m.emplace("codepoints", xivres::util::unicode::convert<std::string>(mapping.Codepoints));
		m.emplace("texts", std::move(texts));
		m.emplace("shape", NameOf(GlyphMergeShapeNames, mapping.Shape));
		m.emplace("textMode", NameOf(GlyphMergeTextModeNames, mapping.TextMode));
		if (mapping.Shape == glyph_merge_shape::Custom) {
			m.emplace("customPath", mapping.CustomPath);
			if (!mapping.CustomSvg.empty())
				m.emplace("customSvg", mapping.CustomSvg);
			m.emplace("customAdvance", mapping.CustomAdvance);
			if (mapping.CustomTextArea)
				m.emplace("customTextArea", *mapping.CustomTextArea);
		} else if (mapping.Shape == glyph_merge_shape::Glyph && mapping.CustomTextArea) {
			m.emplace("customTextArea", *mapping.CustomTextArea);
		}
		mappings.emplace_back(std::move(m));
	}
	json.emplace("mappings", std::move(mappings));
}

void App::Structs::from_json(const nlohmann::json& json, GlyphMergingStruct& value) {
	value = {};
	if (!json.is_object())
		return;

	auto& params = value.Params;
	if (const auto it = json.find("textSize"); it != json.end() && it->is_number())
		params.TextSize = it->get<float>();
	params.FitMode = ValueOf(GlyphMergeFitModeNames, json, "fitMode", glyph_merge_fit_mode::CondenseThenShrink);
	if (const auto it = json.find("textOffset"); it != json.end() && it->is_array() && it->size() == 2) {
		params.TextOffsetX = it->at(0).get<float>();
		params.TextOffsetY = it->at(1).get<float>();
	}
	if (const auto it = json.find("textTransform"); it != json.end())
		from_json(*it, value.TextTransform);
	params.LetterSpacing = json.value<float>("letterSpacing", 0.f);
	params.LineSpacing = json.value<float>("lineSpacing", 0.f);
	params.LineAlignment = ValueOf(GlyphMergeLineAlignmentNames, json, "lineAlignment", glyph_merge_line_alignment::Center);

	if (const auto it = json.find("mappings"); it != json.end() && it->is_array()) {
		for (const auto& m : *it) {
			if (!m.is_object())
				continue;
			auto& mapping = params.Mappings.emplace_back();
			mapping.Codepoints = xivres::util::unicode::convert<std::u32string>(m.value<std::string>("codepoints", ""));
			if (const auto texts = m.find("texts"); texts != m.end() && texts->is_array()) {
				for (const auto& text : *texts)
					mapping.Texts.emplace_back(text.is_string() ? xivres::util::unicode::convert<std::u32string>(text.get<std::string>()) : std::u32string());
			}
			mapping.Shape = ValueOf(GlyphMergeShapeNames, m, "shape", glyph_merge_shape::Box);
			mapping.TextMode = ValueOf(GlyphMergeTextModeNames, m, "textMode", glyph_merge_text_mode::Subtract);
			mapping.CustomPath = m.value<std::string>("customPath", "");
			mapping.CustomSvg = m.value<std::string>("customSvg", "");
			mapping.CustomAdvance = m.value<float>("customAdvance", 1000.f);

			// An area is taken only if it has four finite numbers that span a nonempty rectangle.
			if (const auto it = m.find("customTextArea"); it != m.end() && it->is_array() && it->size() == 4
				&& std::ranges::all_of(*it, [](const nlohmann::json& v) { return v.is_number() && std::isfinite(v.get<float>()); })) {
				const auto area = it->get<std::array<float, 4>>();
				if (area[0] < area[2] && area[1] < area[3])
					mapping.CustomTextArea = area;
			}
		}
	}
}

void App::Structs::from_json(const nlohmann::json& json, RendererSpecificStruct& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	if (const auto obj = json.find("empty"); obj != json.end() && obj->is_object()) {
		value.Empty.Ascent = obj->value<float>("ascent", 0.f);
		value.Empty.LineHeight = obj->value<float>("lineHeight", 0.f);
	} else
		value.Empty = {};
	if (const auto obj = json.find("freetype"); obj != json.end() && obj->is_object()) {
		value.FreeType.LoadFlags = 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noHinting", false) ? FT_LOAD_NO_HINTING : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noBitmap", false) ? FT_LOAD_NO_BITMAP : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("forceAutohint", false) ? FT_LOAD_FORCE_AUTOHINT : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noAutohint", false) ? FT_LOAD_NO_AUTOHINT : 0;
		value.FreeType.RenderMode = static_cast<FT_Render_Mode>(obj->value<int>("renderMode", FT_RENDER_MODE_LIGHT));
	} else
		value.FreeType = {};
	if (const auto obj = json.find("directwrite"); obj != json.end() && obj->is_object()) {
		value.DirectWrite.RenderMode = static_cast<DWRITE_RENDERING_MODE>(obj->value<int>("renderMode", DWRITE_RENDERING_MODE_DEFAULT));
		value.DirectWrite.MeasureMode = static_cast<DWRITE_MEASURING_MODE>(obj->value<int>("measureMode", DWRITE_MEASURING_MODE_GDI_NATURAL));
		value.DirectWrite.GridFitMode = static_cast<DWRITE_GRID_FIT_MODE>(obj->value<int>("gridFitMode", DWRITE_GRID_FIT_MODE_DEFAULT));
	} else
		value.DirectWrite = {};

	value.GlyphImages = {};
	if (const auto obj = json.find("glyphImages"); obj != json.end() && obj->is_object()) {
		auto& glyphImages = value.GlyphImages;
		glyphImages.Path = obj->value<std::string>("path", "");
		for (const auto& [key, v] : {
			     std::pair{"unitsPerEm", &glyphImages.UnitsPerEm},
			     std::pair{"baselineY", &glyphImages.BaselineY},
			     std::pair{"ascent", &glyphImages.Ascent},
			     std::pair{"lineHeight", &glyphImages.LineHeight},
		     }) {
			if (const auto it = obj->find(key); it != obj->end() && it->is_number())
				*v = it->get<float>();
		}
		glyphImages.BitmapCoverage = ValueOf(ImageCoverageModeNames, *obj, "bitmapCoverage", xivres::fontgen::image_coverage_mode::Auto);
		if (const auto embedded = obj->find("embedded"); embedded != obj->end() && embedded->is_object()) {
			if (const auto metadata = embedded->find("metadata"); metadata != embedded->end() && !metadata->is_null())
				glyphImages.EmbeddedMetadata = *metadata;
			if (const auto glyphs = embedded->find("glyphs"); glyphs != embedded->end() && glyphs->is_object()) {
				for (const auto& [key, entry] : glyphs->items()) {
					if (!key.starts_with("U+") || !entry.is_object())
						continue;
					const auto codepoint = static_cast<char32_t>(std::strtoul(key.c_str() + 2, nullptr, 16));
					if (const auto svg = entry.find("svg"); svg != entry.end() && svg->is_string())
						glyphImages.Embedded[codepoint].Svg = std::make_shared<const std::string>(svg->get<std::string>());
					else if (const auto png = entry.find("png"); png != entry.end() && png->is_string())
						glyphImages.Embedded[codepoint].PngBase64 = std::make_shared<const std::string>(png->get<std::string>());
				}
			}
		}
	}
}

void App::Structs::to_json(nlohmann::json& json, const RendererSpecificStruct& value) {
	json = nlohmann::json::object();
	json.emplace("empty", nlohmann::json::object({
		{"ascent", PixelValueToJson(value.Empty.Ascent)},
		{"lineHeight", PixelValueToJson(value.Empty.LineHeight)},
		}));
	json.emplace("freetype", nlohmann::json::object({
		{"noHinting", !!(value.FreeType.LoadFlags & FT_LOAD_NO_HINTING)},
		{"noBitmap", !!(value.FreeType.LoadFlags & FT_LOAD_NO_BITMAP)},
		{"forceAutohint", !!(value.FreeType.LoadFlags & FT_LOAD_FORCE_AUTOHINT)},
		{"noAutohint", !!(value.FreeType.LoadFlags & FT_LOAD_NO_AUTOHINT)},
		{"renderMode", static_cast<int>(value.FreeType.RenderMode)},
		}));
	json.emplace("directwrite", nlohmann::json::object({
		{"renderMode", static_cast<int>(value.DirectWrite.RenderMode)},
		{"measureMode", static_cast<int>(value.DirectWrite.MeasureMode)},
		{"gridFitMode", static_cast<int>(value.DirectWrite.GridFitMode)},
		}));

	const auto& glyphImages = value.GlyphImages;
	if (!glyphImages.Path.empty() || glyphImages.IsEmbedded()) {
		auto obj = nlohmann::json::object();
		obj.emplace("path", glyphImages.Path);
		for (const auto& [key, v] : {
			     std::pair{"unitsPerEm", glyphImages.UnitsPerEm},
			     std::pair{"baselineY", glyphImages.BaselineY},
			     std::pair{"ascent", glyphImages.Ascent},
			     std::pair{"lineHeight", glyphImages.LineHeight},
		     }) {
			if (v)
				obj.emplace(key, *v);
		}
		obj.emplace("bitmapCoverage", NameOf(ImageCoverageModeNames, glyphImages.BitmapCoverage));
		if (glyphImages.IsEmbedded()) {
			auto glyphs = nlohmann::json::object();
			for (const auto& [codepoint, glyph] : glyphImages.Embedded) {
				auto& entry = glyphs[std::format("U+{:04X}", static_cast<uint32_t>(codepoint))];
				if (glyph.Svg)
					entry["svg"] = *glyph.Svg;
				else if (glyph.PngBase64)
					entry["png"] = *glyph.PngBase64;
			}
			obj.emplace("embedded", nlohmann::json::object({
				{"metadata", glyphImages.EmbeddedMetadata.value_or(nlohmann::json())},
				{"glyphs", std::move(glyphs)},
			}));
		}
		json.emplace("glyphImages", std::move(obj));
	}
}

void xivres::fontgen::from_json(const nlohmann::json& json, wrap_modifiers& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Codepoints.clear();
	if (const auto it = json.find("codepoints"); it != json.end() && it->is_array()) {
		for (const auto& v : *it) {
			if (!v.is_array())
				continue;
			switch (v.size()) {
				case 0:
					break;
				case 1:
					value.Codepoints.emplace_back(static_cast<char32_t>(v[0].get<uint32_t>()), static_cast<char32_t>(v[0].get<uint32_t>()));
					break;
				default:
					value.Codepoints.emplace_back(static_cast<char32_t>(v[0].get<uint32_t>()), static_cast<char32_t>(v[1].get<uint32_t>()));
					break;
			}
		}
	}

	value.LetterSpacing = json.value<float>("letterSpacing", 0.f);
	value.HorizontalOffset = json.value<float>("horizontalOffset", 0.f);
	value.BaselineShift = json.value<float>("baselineShift", 0.f);

	value.Monospacing = {};
	if (const auto it = json.find("monospacing"); it != json.end() && it->is_object()) {
		auto& m = value.Monospacing;
		if (const auto v = it->find("min"); v != it->end() && v->is_number())
			m.MinAdvance = v->get<float>();
		if (const auto v = it->find("max"); v != it->end() && v->is_number())
			m.MaxAdvance = v->get<float>();

		const auto unit = it->value<std::string>("unit", "em");
		m.Unit = unit == "px"
			? monospacing_unit::Pixels
			: unit == "glyph"
			? monospacing_unit::ReferenceGlyph
			: monospacing_unit::Em;

		if (const auto s = it->value<std::string>("referenceChar", ""); !s.empty()) {
			char32_t c = util::unicode::UReplacement;
			util::unicode::decode(c, s.c_str(), s.size());
			if (c != util::unicode::UReplacement)
				m.ReferenceCharacter = c;
		}

		const auto alignment = it->value<std::string>("alignment", "centerAdvance");
		m.Alignment = alignment == "left"
			? monospacing_alignment::Left
			: alignment == "centerInk"
			? monospacing_alignment::CenterInk
			: alignment == "right"
			? monospacing_alignment::Right
			: monospacing_alignment::CenterAdvance;

		m.DropKerning = it->value<bool>("dropKerning", true);
	}

	value.CodepointReplacements.clear();
	if (const auto it = json.find("codepointReplacements"); it != json.end() && it->is_object()) {
		for (const auto& item : it->items()) {
			auto vs = item.value().get<std::string>();
			char32_t l = util::unicode::UReplacement;
			char32_t r = util::unicode::UReplacement;
			util::unicode::decode(l, item.key().c_str(), item.key().size());
			util::unicode::decode(r, vs.c_str(), vs.size());
			if (l == util::unicode::UReplacement || r == util::unicode::UReplacement)
				continue;

			value.CodepointReplacements.emplace(l, r);
		}
	}
}

void xivres::fontgen::to_json(nlohmann::json& json, const wrap_modifiers& value) {
	json = nlohmann::json::object();
	auto& codepoints = *json.emplace("codepoints", nlohmann::json::array()).first;
	for (const auto& c : value.Codepoints)
		codepoints.emplace_back(nlohmann::json::array({ static_cast<uint32_t>(c.first), static_cast<uint32_t>(c.second) }));
	json.emplace("letterSpacing", PixelValueToJson(value.LetterSpacing));
	json.emplace("horizontalOffset", PixelValueToJson(value.HorizontalOffset));
	json.emplace("baselineShift", PixelValueToJson(value.BaselineShift));
	if (const auto& m = value.Monospacing; m.is_enabled()) {
		auto& obj = *json.emplace("monospacing", nlohmann::json::object()).first;
		if (m.MinAdvance)
			obj.emplace("min", *m.MinAdvance);
		if (m.MaxAdvance)
			obj.emplace("max", *m.MaxAdvance);
		switch (m.Unit) {
			case monospacing_unit::Pixels: obj.emplace("unit", "px"); break;
			case monospacing_unit::Em: obj.emplace("unit", "em"); break;
			case monospacing_unit::ReferenceGlyph: obj.emplace("unit", "glyph"); break;
		}
		obj.emplace("referenceChar", xivres::util::unicode::convert_from_codepoint<std::string>(m.ReferenceCharacter));
		switch (m.Alignment) {
			case monospacing_alignment::Left: obj.emplace("alignment", "left"); break;
			case monospacing_alignment::CenterAdvance: obj.emplace("alignment", "centerAdvance"); break;
			case monospacing_alignment::CenterInk: obj.emplace("alignment", "centerInk"); break;
			case monospacing_alignment::Right: obj.emplace("alignment", "right"); break;
		}
		obj.emplace("dropKerning", m.DropKerning);
	}
	auto& codepointReplacements = *json.emplace("codepointReplacements", nlohmann::json::object()).first;
	for (const auto& [from, to] : value.CodepointReplacements)
		codepointReplacements[xivres::util::unicode::convert_from_codepoint<std::string>(from)] = xivres::util::unicode::convert_from_codepoint<std::string>(to);
}

void App::Structs::from_json(const nlohmann::json& json, LookupStruct& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Name = json.value<std::string>("name", "");
	value.Weight = static_cast<DWRITE_FONT_WEIGHT>(json.value<int>("weight", DWRITE_FONT_WEIGHT_NORMAL));
	value.Stretch = static_cast<DWRITE_FONT_STRETCH>(json.value<int>("stretch", DWRITE_FONT_STRETCH_NORMAL));
	value.Style = static_cast<DWRITE_FONT_STYLE>(json.value<int>("style", DWRITE_FONT_STYLE_NORMAL));
	value.Features.clear();
	if (const auto it = json.find("features"); it != json.end() && it->is_array()) {
		for (const auto& [_, v] : it->items()) {
			auto vs = v.get<std::string>();
			vs.resize(4, ' ');
			value.Features.emplace(static_cast<DWRITE_FONT_FEATURE_TAG>(*reinterpret_cast<const uint32_t*>(vs.c_str())), 1);
		}
	}

	// Values other than 1 are stored separately, so that older versions still read the list of features.
	// A feature turned off appears only here, with 0, so that older versions leave it at the font default
	// instead of turning it on.
	if (const auto it = json.find("featureValues"); it != json.end() && it->is_object()) {
		for (const auto& [name, v] : it->items()) {
			if (!v.is_number_unsigned())
				continue;
			std::string tag = name;
			tag.resize(4, ' ');
			const auto key = static_cast<DWRITE_FONT_FEATURE_TAG>(*reinterpret_cast<const uint32_t*>(tag.c_str()));
			value.Features[key] = v.get<uint32_t>();
		}
	}
	value.Language = json.value<std::string>("language", "");
	value.Variations.clear();
	if (const auto it = json.find("variations"); it != json.end() && it->is_object()) {
		for (const auto& [tag, v] : it->items()) {
			if (v.is_number())
				value.Variations[tag] = v.get<float>();
		}
	}
	value.Synthesis.reset();
	if (const auto it = json.find("synthesis"); it != json.end() && it->is_object())
		value.Synthesis = SynthesisStruct{.Allow = it->value<bool>("allow", true)};
}

void App::Structs::to_json(nlohmann::json& json, const LookupStruct& value) {
	json = nlohmann::json::object();
	json.emplace("name", value.Name);
	json.emplace("weight", static_cast<int>(value.Weight));
	json.emplace("stretch", static_cast<int>(value.Stretch));
	json.emplace("style", static_cast<int>(value.Style));

	auto features = nlohmann::json::array();
	auto featureValues = nlohmann::json::object();
	for (const auto& [tag, v] : value.Features) {
		char buf[5]{};
		*reinterpret_cast<uint32_t*>(buf) = static_cast<uint32_t>(tag);
		if (v != 0)
			features.emplace_back(buf);
		if (v != 1)
			featureValues[buf] = v;
	}
	json.emplace("features", features);
	if (!featureValues.empty())
		json.emplace("featureValues", std::move(featureValues));
	if (!value.Language.empty())
		json.emplace("language", value.Language);
	if (!value.Variations.empty()) {
		auto variations = nlohmann::json::object();
		for (const auto& [tag, v] : value.Variations)
			variations[tag] = v;
		json.emplace("variations", std::move(variations));
	}
	if (value.Synthesis)
		json.emplace("synthesis", nlohmann::json::object({{"allow", value.Synthesis->Allow}}));
}

void App::Structs::from_json(const nlohmann::json& json, MultiFontSet& value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.FontSets.clear();
	if (json.contains("faces")) {
		value.FontSets.emplace_back(std::make_unique<FontSet>(std::move(json.get<FontSet>())));
		return;
	}

	if (const auto it = json.find("fontSets"); it != json.end() && it->is_array()) {
		for (const auto& o : *it) {
			value.FontSets.emplace_back(std::make_unique<FontSet>(o.get<FontSet>()));
		}
	}

	value.ExportMapFontLobbyToFont = json.value("exportMapFontLobbyToFont", false);
	value.ExportMapChnAxisToFont = json.value("exportMapChnAxisToFont", false);
	value.ExportMapKrnAxisToFont = json.value("exportMapKrnAxisToFont", false);
	value.ExportMapTcAxisToFont = json.value("exportMapTcAxisToFont", false);
}

void App::Structs::to_json(nlohmann::json& json, const MultiFontSet& value) {
	json = nlohmann::json::object();
	auto& fontSets = json["fontSets"] = nlohmann::json::array();
	for (const auto& set : value.FontSets) {
		to_json(fontSets.emplace_back(), *set);
	}

	json["exportMapFontLobbyToFont"] = value.ExportMapFontLobbyToFont;
	json["exportMapChnAxisToFont"] = value.ExportMapChnAxisToFont;
	json["exportMapKrnAxisToFont"] = value.ExportMapKrnAxisToFont;
	json["exportMapTcAxisToFont"] = value.ExportMapTcAxisToFont;
}

const char* App::Structs::GetDefaultPreviewText() {
	return reinterpret_cast<const char*>(
		u8"!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~\r\n"
		u8"0英en-US: _The_ (89) quick brown foxes jump over the [01] lazy dogs.\r\n"
		u8"1日ja-JP: _パングラム_(pangram)で[23]つの字体（フォント）をテストします。\r\n"
		u8"2中zh-CN: _(天)地玄黃_，宇[宙]洪荒。蓋此身髮，4大5常。\r\n"
		u8"3韓ko-KR: 45 _다(람)쥐_ 67 헌 쳇바퀴에 타[고]파.\r\n"
		u8"4露ru-RU: Съешь (ж)е ещё этих мягких 23 [ф]ранцузских булок да 45 выпей чаю.\r\n"
		u8"5泰th-TH: เป็นมนุษย์สุดประเสริฐเลิศคุณค่า\r\n"
		u8"6\r\n"
		u8"7\r\n"
		u8"8\r\n"
		u8"9\r\n"
		u8"\r\n"
		u8"\r\n"
		// almost every script covered by Nirmala UI has too much triple character gpos entries to be usable in game, so don't bother
		);
}
