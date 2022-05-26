#include "pch.h"
#include "Structs.h"

std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont> GetGameFont(XivRes::FontGenerator::GameFontFamily family, float size) {
	static std::map<XivRes::GameFontType, XivRes::FontGenerator::GameFontdataSet> s_fontSet;
	static std::mutex s_mtx;

	const auto lock = std::lock_guard(s_mtx);

	std::shared_ptr<XivRes::FontGenerator::GameFontdataSet> strong;

	auto pathconf = nlohmann::json::object();
	pathconf["global"] = nlohmann::json::array({ R"(C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game)" });
	pathconf["chinese"] = nlohmann::json::array({
		reinterpret_cast<const char*>(u8R"(C:\Program Files (x86)\上海数龙科技有限公司\最终幻想XIV\)"),
		R"(C:\Program Files (x86)\SNDA\FFXIV\game)",
		});
	pathconf["korean"] = nlohmann::json::array({ R"(C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game)" });

	try {
		if (!exists(std::filesystem::path("config.json"))) {
			std::ofstream out("config.json");
			out << pathconf;
		} else {
			std::ifstream in("config.json");
			pathconf = nlohmann::json::parse(in);
		}
	} catch (...) {

	}

	try {
		switch (family) {
			case XivRes::FontGenerator::GameFontFamily::AXIS:
			case XivRes::FontGenerator::GameFontFamily::Jupiter:
			case XivRes::FontGenerator::GameFontFamily::JupiterN:
			case XivRes::FontGenerator::GameFontFamily::MiedingerMid:
			case XivRes::FontGenerator::GameFontFamily::Meidinger:
			case XivRes::FontGenerator::GameFontFamily::TrumpGothic:
			{
				auto& font = s_fontSet[XivRes::GameFontType::font];
				if (!font) {
					for (const auto& path : pathconf["global"]) {
						try {
							font = XivRes::GameReader(path.get<std::string>()).GetFonts(XivRes::GameFontType::font);
						} catch (...) {
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.GetFont(family, size);
			}

			case XivRes::FontGenerator::GameFontFamily::ChnAXIS:
			{
				auto& font = s_fontSet[XivRes::GameFontType::chn_axis];
				if (!font) {
					for (const auto& path : pathconf["chinese"]) {
						try {
							font = XivRes::GameReader(path.get<std::string>()).GetFonts(XivRes::GameFontType::chn_axis);
						} catch (...) {
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.GetFont(family, size);
			}

			case XivRes::FontGenerator::GameFontFamily::KrnAXIS:
			{
				auto& font = s_fontSet[XivRes::GameFontType::krn_axis];
				if (!font) {
					for (const auto& path : pathconf["korean"]) {
						try {
							font = XivRes::GameReader(path.get<std::string>()).GetFonts(XivRes::GameFontType::krn_axis);
						} catch (...) {
						}
					}
					if (!font)
						throw std::runtime_error("Font not found in given path");
				}
				return font.GetFont(family, size);
			}
		}
	} catch (const std::exception& e) {
		static bool showed = false;
		if (!showed) {
			showed = true;
			MessageBoxW(nullptr, std::format(
				L"Failed to find corresponding game installation ({}). Specify it in config.json. Delete config.json and run this program again to start anew. Suppressing this message from now on.",
				XivRes::Unicode::Convert<std::wstring>(e.what())).c_str(), L"Error", MB_OK);
		}
	}

	return std::make_shared<XivRes::FontGenerator::EmptyFixedSizeFont>(size, XivRes::FontGenerator::EmptyFixedSizeFont::CreateStruct{});
}

std::wstring App::Structs::LookupStruct::GetWeightString() const {
	switch (Weight) {
		case DWRITE_FONT_WEIGHT_THIN: return L"Thin";
		case DWRITE_FONT_WEIGHT_EXTRA_LIGHT: return L"Extra Light";
		case DWRITE_FONT_WEIGHT_LIGHT: return L"Light";
		case DWRITE_FONT_WEIGHT_SEMI_LIGHT: return L"Semi Light";
		case DWRITE_FONT_WEIGHT_NORMAL: return L"Normal";
		case DWRITE_FONT_WEIGHT_MEDIUM: return L"Medium";
		case DWRITE_FONT_WEIGHT_SEMI_BOLD: return L"Semi Bold";
		case DWRITE_FONT_WEIGHT_BOLD: return L"Bold";
		case DWRITE_FONT_WEIGHT_EXTRA_BOLD:return L"Extra Bold";
		case DWRITE_FONT_WEIGHT_BLACK: return L"Black";
		case DWRITE_FONT_WEIGHT_EXTRA_BLACK: return L"Extra Black";
		default: return std::format(L"{}", static_cast<int>(Weight));
	}
}

std::wstring App::Structs::LookupStruct::GetStretchString() const {
	switch (Stretch) {
		case DWRITE_FONT_STRETCH_UNDEFINED: return L"Undefined";
		case DWRITE_FONT_STRETCH_ULTRA_CONDENSED: return L"Ultra Condensed";
		case DWRITE_FONT_STRETCH_EXTRA_CONDENSED: return L"Extra Condensed";
		case DWRITE_FONT_STRETCH_CONDENSED: return L"Condensed";
		case DWRITE_FONT_STRETCH_SEMI_CONDENSED: return L"Semi Condensed";
		case DWRITE_FONT_STRETCH_NORMAL: return L"Normal";
		case DWRITE_FONT_STRETCH_SEMI_EXPANDED: return L"Semi Expanded";
		case DWRITE_FONT_STRETCH_EXPANDED: return L"Expanded";
		case DWRITE_FONT_STRETCH_EXTRA_EXPANDED: return L"Extra Expanded";
		case DWRITE_FONT_STRETCH_ULTRA_EXPANDED: return L"Ultra Expanded";
		default: return L"Invalid";
	}
}

std::wstring App::Structs::LookupStruct::GetStyleString() const {
	switch (Style) {
		case DWRITE_FONT_STYLE_NORMAL: return L"Normal";
		case DWRITE_FONT_STYLE_OBLIQUE: return L"Oblique";
		case DWRITE_FONT_STYLE_ITALIC: return L"Italic";
		default: return L"Invalid";
	}
}

std::pair<IDWriteFactoryPtr, IDWriteFontPtr> App::Structs::LookupStruct::ResolveFont() const {
	using namespace XivRes::FontGenerator;

	IDWriteFactoryPtr factory;
	SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)));

	IDWriteFontCollectionPtr coll;
	SuccessOrThrow(factory->GetSystemFontCollection(&coll));

	uint32_t index;
	BOOL exists;
	SuccessOrThrow(coll->FindFamilyName(XivRes::Unicode::Convert<std::wstring>(Name).c_str(), &index, &exists));
	if (!exists)
		throw std::invalid_argument("Font not found");

	IDWriteFontFamilyPtr family;
	SuccessOrThrow(coll->GetFontFamily(index, &family));

	IDWriteFontPtr font;
	SuccessOrThrow(family->GetFirstMatchingFont(Weight, Stretch, Style, &font));

	return std::make_pair(std::move(factory), std::move(font));
}

std::pair<std::shared_ptr<XivRes::IStream>, int> App::Structs::LookupStruct::ResolveStream() const {
	using namespace XivRes::FontGenerator;

	auto [factory, font] = ResolveFont();

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

	auto res = std::make_shared<XivRes::MemoryStream>();
	uint64_t fileSize;
	SuccessOrThrow(stream->GetFileSize(&fileSize));
	const void* pFragmentStart;
	void* pFragmentContext;
	SuccessOrThrow(stream->ReadFileFragment(&pFragmentStart, 0, fileSize, &pFragmentContext));
	std::vector<uint8_t> buf(static_cast<size_t>(fileSize));
	memcpy(&buf[0], pFragmentStart, buf.size());
	stream->ReleaseFileFragment(pFragmentContext);

	return { std::make_shared<XivRes::MemoryStream>(std::move(buf)), face->GetIndex() };
}

const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& App::Structs::FaceElement::GetBaseFont() const {
	if (!m_baseFont) {
		try {
			switch (Renderer) {
				case RendererEnum::Empty:
					m_baseFont = std::make_shared<XivRes::FontGenerator::EmptyFixedSizeFont>(Size, RendererSpecific.Empty);
					break;

				case RendererEnum::PrerenderedGameInstallation:
					if (Lookup.Name == "AXIS")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::AXIS, Size);
					else if (Lookup.Name == "Jupiter")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::Jupiter, Size);
					else if (Lookup.Name == "JupiterN")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::JupiterN, Size);
					else if (Lookup.Name == "Meidinger")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::Meidinger, Size);
					else if (Lookup.Name == "MiedingerMid")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::MiedingerMid, Size);
					else if (Lookup.Name == "TrumpGothic")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::TrumpGothic, Size);
					else if (Lookup.Name == "ChnAXIS")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::ChnAXIS, Size);
					else if (Lookup.Name == "KrnAXIS")
						m_baseFont = GetGameFont(XivRes::FontGenerator::GameFontFamily::KrnAXIS, Size);
					else
						throw std::runtime_error("Invalid name");
					break;

				case RendererEnum::DirectWrite:
				{
					auto [factory, font] = Lookup.ResolveFont();
					m_baseFont = std::make_shared<XivRes::FontGenerator::DirectWriteFixedSizeFont>(std::move(factory), std::move(font), Size, Gamma, RendererSpecific.DirectWrite);
					break;
				}

				case RendererEnum::FreeType:
				{
					auto [pStream, index] = Lookup.ResolveStream();
					m_baseFont = std::make_shared<XivRes::FontGenerator::FreeTypeFixedSizeFont>(*pStream, index, Size, Gamma, RendererSpecific.FreeType);
					break;
				}

				default:
					m_baseFont = std::make_shared<XivRes::FontGenerator::EmptyFixedSizeFont>();
					break;
			}

		} catch (...) {
			m_baseFont = std::make_shared<XivRes::FontGenerator::EmptyFixedSizeFont>(Size, RendererSpecific.Empty);
		}
	}

	return m_baseFont;
}


const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& App::Structs::FaceElement::GetWrappedFont() const {
	if (!m_wrappedFont)
		m_wrappedFont = std::make_shared<XivRes::FontGenerator::WrappingFixedSizeFont>(GetBaseFont(), WrapModifiers);

	return m_wrappedFont;
}

void App::Structs::FaceElement::OnFontWrappingParametersChange() {
	m_wrappedFont = nullptr;
}

void App::Structs::FaceElement::OnFontCreateParametersChange() {
	m_wrappedFont = nullptr;
	m_baseFont = nullptr;
}

std::string App::Structs::FaceElement::GetBaseFontKey() const {
	switch (Renderer) {
		case RendererEnum::Empty:
			return std::format("empty:{:g}:{}:{}", Size, RendererSpecific.Empty.Ascent, RendererSpecific.Empty.LineHeight);
			break;
		case RendererEnum::PrerenderedGameInstallation:
			return std::format("game:{}:{:g}", Lookup.Name, Size);
			break;
		case RendererEnum::DirectWrite:
			return std::format("directwrite:{}:{:g}:{:g}:{}:{}:{}:{}:{}:{}",
				Lookup.Name,
				Size,
				Gamma,
				static_cast<uint32_t>(Lookup.Weight),
				static_cast<uint32_t>(Lookup.Stretch),
				static_cast<uint32_t>(Lookup.Style),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.RenderMode),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.MeasureMode),
				static_cast<uint32_t>(RendererSpecific.DirectWrite.GridFitMode)
			);
			break;
		case RendererEnum::FreeType:
			return std::format("freetype:{}:{:g}:{:g}:{}:{}:{}:{}",
				Lookup.Name,
				Size,
				Gamma,
				static_cast<uint32_t>(Lookup.Weight),
				static_cast<uint32_t>(Lookup.Stretch),
				static_cast<uint32_t>(Lookup.Style),
				static_cast<uint32_t>(RendererSpecific.FreeType.LoadFlags)
			);
			break;
		default:
			throw std::runtime_error("Invalid renderer");
	}
}

std::wstring App::Structs::FaceElement::GetRangeRepresentation() const {
	if (WrapModifiers.Codepoints.empty())
		return L"(None)";

	std::wstring res;
	std::vector<char32_t> charVec(GetBaseFont()->GetAllCodepoints().begin(), GetBaseFont()->GetAllCodepoints().end());
	for (const auto& [c1, c2] : WrapModifiers.Codepoints) {
		if (!res.empty())
			res += L", ";

		const auto left = std::ranges::lower_bound(charVec, c1);
		const auto right = std::ranges::upper_bound(charVec, c2);
		const auto count = right - left;

		const auto blk = std::lower_bound(XivRes::Unicode::UnicodeBlocks::Blocks.begin(), XivRes::Unicode::UnicodeBlocks::Blocks.end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
		if (blk != XivRes::Unicode::UnicodeBlocks::Blocks.end() && blk->First == c1 && blk->Last == c2) {
			res += std::format(L"{}({})", XivRes::Unicode::Convert<std::wstring>(blk->Name), count);
		} else if (c1 == c2) {
			res += std::format(
				L"U+{:04X} [{}]",
				static_cast<uint32_t>(c1),
				XivRes::Unicode::RepresentChar<std::wstring>(c1)
			);
		} else {
			res += std::format(
				L"U+{:04X}~{:04X} ({}) {} ~ {}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				count,
				XivRes::Unicode::RepresentChar<std::wstring>(c1),
				XivRes::Unicode::RepresentChar<std::wstring>(c2)
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
			return std::format(L"DirectWrite (Render={}, Measure={}, GridFit={})",
				RendererSpecific.DirectWrite.GetRenderModeString(),
				RendererSpecific.DirectWrite.GetMeasuringModeString(),
				RendererSpecific.DirectWrite.GetGridFitModeString()
			);

		case RendererEnum::FreeType:
			return std::format(L"FreeType ({})", RendererSpecific.FreeType.GetLoadFlagsString());

		default:
			return L"INVALID";
	}
}

std::wstring App::Structs::FaceElement::GetLookupRepresentation() const {
	switch (Renderer) {
		case RendererEnum::DirectWrite:
		case RendererEnum::FreeType:
			return std::format(L"{} ({}, {}, {})",
				XivRes::Unicode::Convert<std::wstring>(Lookup.Name),
				Lookup.GetWeightString(),
				Lookup.GetStyleString(),
				Lookup.GetStretchString()
			);

		default:
			return L"-";
	}
}

App::Structs::FaceElement::FaceElement() noexcept = default;

App::Structs::FaceElement::FaceElement(FaceElement && r) noexcept : FaceElement() {
	swap(*this, r);
}

App::Structs::FaceElement::FaceElement(const FaceElement & r)
	: m_baseFont(r.m_baseFont)
	, m_wrappedFont(r.m_wrappedFont)
	, Size(r.Size)
	, Gamma(r.Gamma)
	, Overwrite(r.Overwrite)
	, WrapModifiers(r.WrapModifiers)
	, Renderer(r.Renderer)
	, Lookup(r.Lookup)
	, RendererSpecific(r.RendererSpecific) {
}

App::Structs::FaceElement App::Structs::FaceElement::operator=(FaceElement && r) noexcept {
	swap(*this, r);
	return *this;
}

App::Structs::FaceElement App::Structs::FaceElement::operator=(const FaceElement & r) {
	auto r2(r);
	swap(*this, r2);
	return *this;
}

void App::Structs::swap(App::Structs::FaceElement & l, App::Structs::FaceElement & r) noexcept {
	if (&l == &r)
		return;

	using std::swap;
	swap(l.m_baseFont, r.m_baseFont);
	swap(l.m_wrappedFont, r.m_wrappedFont);
	swap(l.Size, r.Size);
	swap(l.Gamma, r.Gamma);
	swap(l.Overwrite, r.Overwrite);
	swap(l.WrapModifiers, r.WrapModifiers);
	swap(l.Renderer, r.Renderer);
	swap(l.Lookup, r.Lookup);
	swap(l.RendererSpecific, r.RendererSpecific);
}

const std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>& App::Structs::Face::GetMergedFont() const {
	if (!MergedFont) {
		std::vector<std::pair<std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>, bool>> mergeFontList;

		for (auto& pElement : Elements)
			mergeFontList.emplace_back(pElement->GetWrappedFont(), pElement->Overwrite);

		MergedFont = std::make_shared<XivRes::FontGenerator::MergedFixedSizeFont>(std::move(mergeFontList));
	}

	return MergedFont;
}

void App::Structs::Face::OnElementChange() {
	MergedFont = nullptr;
}

App::Structs::Face::Face() noexcept = default;

App::Structs::Face::Face(Face && r) noexcept : Face() {
	swap(*this, r);
}

App::Structs::Face::Face(const Face & r)
	: MergedFont(r.MergedFont)
	, PreviewText(r.PreviewText) {

	Elements.reserve(r.Elements.size());
	for (const auto& e : r.Elements)
		Elements.emplace_back(std::make_unique<FaceElement>(*e));
}

App::Structs::Face& App::Structs::Face::operator=(Face && r) noexcept {
	swap(*this, r);
	return *this;
}

App::Structs::Face& App::Structs::Face::operator=(const Face & r) {
	auto r2(r);
	swap(*this, r2);
	return *this;
}

void App::Structs::swap(App::Structs::Face& l, App::Structs::Face& r) noexcept {
	if (&l == &r)
		return;

	using std::swap;
	swap(l.Name, r.Name);
	swap(l.PreviewText, r.PreviewText);
	swap(l.Elements, r.Elements);
}

void App::Structs::FontSet::ConsolidateFonts() {
	std::map<std::string, std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>> loadedBaseFonts;
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

App::Structs::FontSet App::Structs::FontSet::NewFromTemplateFont(XivRes::GameFontType fontType) {
	FontSet res{};

	if (const auto pcszFmt = XivRes::FontGenerator::GetFontTexFilenameFormat(fontType)) {
		std::string_view filename(pcszFmt);
		filename = filename.substr(filename.rfind('/') + 1);
		res.TexFilenameFormat = filename;
	} else
		res.TexFilenameFormat = "font{}.tex";

	for (const auto& def : XivRes::FontGenerator::GetFontDefinition(fontType)) {
		std::string_view filename(def.Path);
		filename = filename.substr(filename.rfind('/') + 1);
		filename = filename.substr(0, filename.find('.'));

		std::string previewText;
		std::vector<std::pair<char32_t, char32_t>> codepoints;
		if (filename == "Jupiter_45" || filename == "Jupiter_90") {
			previewText = "123,456,789.000!!!";
			codepoints = { { U'0', U'9' }, { U'!', U'!' }, { U'.', U'.' }, { U',', U',' } };
		} else if (filename.starts_with("Meidinger_")) {
			previewText = "0123456789?!%+-./";
			codepoints = { { 0x0000, 0x10FFFF } };
		} else {
			previewText = GetDefaultPreviewText();
			codepoints = { { 0x0000, 0x10FFFF } };
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

void App::Structs::from_json(const nlohmann::json & json, FontSet & value) {
	if (!json.is_object()) {
		value = {};
		return;
	}

	value.Faces.clear();
	if (const auto it = json.find("faces"); it != json.end() && it->is_array()) {
		for (const auto& v : *it)
			value.Faces.emplace_back(std::make_unique<Face>(v.get<Face>()));
	}
	value.TexFilenameFormat = json.value<std::string>("texFilenameFormat", "");
}

void App::Structs::to_json(nlohmann::json & json, const FontSet & value) {
	json = nlohmann::json::object();
	auto& faces = *json.emplace("faces", nlohmann::json::array()).first;
	for (const auto& e : value.Faces)
		faces.emplace_back(*e);
	json.emplace("texFilenameFormat", value.TexFilenameFormat);
}

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
}

void App::Structs::to_json(nlohmann::json & json, const Face & value) {
	json = nlohmann::json::object();
	json.emplace("name", value.Name);
	auto& elements = *json.emplace("elements", nlohmann::json::array()).first;
	for (const auto& e : value.Elements)
		elements.emplace_back(*e);
	json.emplace("previewText", value.PreviewText);
}

void App::Structs::from_json(const nlohmann::json & json, FaceElement & value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Size = json.value<float>("size", 0.f);
	value.Gamma = json.value<float>("gamma", 1.f);
	value.Overwrite = json.value<bool>("overwrite", false);
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
}

void App::Structs::to_json(nlohmann::json & json, const FaceElement & value) {
	json = nlohmann::json::object();
	json.emplace("size", value.Size);
	json.emplace("gamma", value.Gamma);
	json.emplace("overwrite", value.Overwrite);
	json.emplace("wrapModifiers", value.WrapModifiers);
	json.emplace("renderer", static_cast<int>(value.Renderer));
	json.emplace("lookup", value.Lookup);
	json.emplace("renderSpecific", value.RendererSpecific);
}

void App::Structs::from_json(const nlohmann::json & json, RendererSpecificStruct & value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	if (const auto obj = json.find("empty"); obj != json.end() && obj->is_object()) {
		value.Empty.Ascent = obj->value<int>("ascent", 0);
		value.Empty.LineHeight = obj->value<int>("lineHeight", 0);
	} else
		value.Empty = {};
	if (const auto obj = json.find("freetype"); obj != json.end() && obj->is_object()) {
		value.FreeType.LoadFlags = 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noHinting", false) ? FT_LOAD_NO_HINTING : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noBitmap", false) ? FT_LOAD_NO_BITMAP : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("forceAutohint", false) ? FT_LOAD_FORCE_AUTOHINT : 0;
		value.FreeType.LoadFlags |= obj->value<bool>("noAutohint", false) ? FT_LOAD_NO_AUTOHINT : 0;
	} else
		value.FreeType = {};
	if (const auto obj = json.find("directwrite"); obj != json.end() && obj->is_object()) {
		value.DirectWrite.RenderMode = static_cast<DWRITE_RENDERING_MODE>(obj->value<int>("renderMode", DWRITE_RENDERING_MODE_DEFAULT));
		value.DirectWrite.MeasureMode = static_cast<DWRITE_MEASURING_MODE>(obj->value<int>("measureMode", DWRITE_MEASURING_MODE_GDI_NATURAL));
		value.DirectWrite.GridFitMode = static_cast<DWRITE_GRID_FIT_MODE>(obj->value<int>("gridFitMode", DWRITE_GRID_FIT_MODE_DEFAULT));
	} else
		value.DirectWrite = {};
}

void App::Structs::to_json(nlohmann::json & json, const RendererSpecificStruct & value) {
	json = nlohmann::json::object();
	json.emplace("empty", nlohmann::json::object({
		{ "ascent", value.Empty.Ascent },
		{ "lineHeight", value.Empty.LineHeight },
		}));
	json.emplace("freetype", nlohmann::json::object({
		{ "noHinting", !!(value.FreeType.LoadFlags & FT_LOAD_NO_HINTING) },
		{ "noBitmap", !!(value.FreeType.LoadFlags & FT_LOAD_NO_BITMAP) },
		{ "forceAutohint", !!(value.FreeType.LoadFlags & FT_LOAD_FORCE_AUTOHINT) },
		{ "noAutohint", !!(value.FreeType.LoadFlags & FT_LOAD_NO_AUTOHINT) },
		}));
	json.emplace("directwrite", nlohmann::json::object({
		{ "renderMode", static_cast<int>(value.DirectWrite.RenderMode) },
		{ "measureMode", static_cast<int>(value.DirectWrite.MeasureMode) },
		{ "gridFitMode", static_cast<int>(value.DirectWrite.GridFitMode) },
		}));
}

void XivRes::FontGenerator::from_json(const nlohmann::json & json, XivRes::FontGenerator::WrapModifiers & value) {
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

	value.LetterSpacing = json.value<int>("letterSpacing", 0);
	value.HorizontalOffset = json.value<int>("horizontalOffset", 0);
	value.BaselineShift = json.value<int>("baselineShift", 0);
}

void XivRes::FontGenerator::to_json(nlohmann::json & json, const XivRes::FontGenerator::WrapModifiers & value) {
	json = nlohmann::json::object();
	auto& codepoints = *json.emplace("codepoints", nlohmann::json::array()).first;
	for (const auto& c : value.Codepoints)
		codepoints.emplace_back(nlohmann::json::array({ static_cast<uint32_t>(c.first), static_cast<uint32_t>(c.second) }));
	json.emplace("letterSpacing", value.LetterSpacing);
	json.emplace("horizontalOffset", value.HorizontalOffset);
	json.emplace("baselineShift", value.BaselineShift);
}

void App::Structs::from_json(const nlohmann::json & json, LookupStruct & value) {
	if (!json.is_object())
		throw std::runtime_error(std::format("Expected an object, got {}", json.type_name()));

	value.Name = json.value<std::string>("name", "");
	value.Weight = static_cast<DWRITE_FONT_WEIGHT>(json.value<int>("weight", DWRITE_FONT_WEIGHT_NORMAL));
	value.Stretch = static_cast<DWRITE_FONT_STRETCH>(json.value<int>("stretch", DWRITE_FONT_STRETCH_NORMAL));
	value.Style = static_cast<DWRITE_FONT_STYLE>(json.value<int>("style", DWRITE_FONT_STYLE_NORMAL));
}

void App::Structs::to_json(nlohmann::json & json, const LookupStruct & value) {
	json = nlohmann::json::object();
	json.emplace("name", value.Name);
	json.emplace("weight", static_cast<int>(value.Weight));
	json.emplace("stretch", static_cast<int>(value.Stretch));
	json.emplace("style", static_cast<int>(value.Style));
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
