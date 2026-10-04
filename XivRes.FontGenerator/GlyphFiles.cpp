#include "pch.h"
#include "GlyphFiles.h"

#include "WicImage.h"

namespace {
	using xivres::fontgen::glyph_metrics;
	using xivres::fontgen::glyph_outline;

	// Up to three decimals, without trailing zeros.
	std::string FormatNumber(double value) {
		auto s = std::format("{:.3f}", value);
		while (s.ends_with('0'))
			s.pop_back();
		if (s.ends_with('.'))
			s.pop_back();
		if (s == "-0")
			s = "0";
		return s;
	}

	std::string FormatCodepoint(char32_t codepoint) {
		return std::format("U+{:04X}", static_cast<uint32_t>(codepoint));
	}

	std::string EncodeBase64(std::span<const uint8_t> data) {
		static constexpr char Alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
		std::string res;
		res.reserve((data.size() + 2) / 3 * 4);
		for (size_t i = 0; i < data.size(); i += 3) {
			const auto n = data.size() - i;
			const uint32_t v = data[i] << 16 | (n > 1 ? data[i + 1] << 8 : 0) | (n > 2 ? data[i + 2] : 0);
			res += Alphabet[v >> 18 & 63];
			res += Alphabet[v >> 12 & 63];
			res += n > 1 ? Alphabet[v >> 6 & 63] : '=';
			res += n > 2 ? Alphabet[v & 63] : '=';
		}
		return res;
	}

	// Maps pixels of the font, in the space of glyph_metrics, to the units of the SVG files.
	struct UnitMapping {
		double Scale;  // units per pixel
		int Ascent;

		[[nodiscard]] double X(double px) const { return px * Scale; }
		[[nodiscard]] double Y(double px) const { return App::GlyphFiles::DefaultBaselineY + (px - Ascent) * Scale; }
	};

	std::string OutlineToPathData(const glyph_outline& outline, const UnitMapping& m) {
		std::string res;
		size_t pointIndex = 0;
		const auto appendPoints = [&](char command, size_t count) {
			if (!res.empty())
				res += ' ';
			res += command;
			for (size_t i = 0; i < count; i++, pointIndex++) {
				const auto& p = outline.Points[pointIndex];
				res += std::format("{}{},{}", i ? " " : "", FormatNumber(m.X(p.X)), FormatNumber(m.Y(p.Y)));
			}
		};
		for (const auto verb : outline.Verbs) {
			switch (verb) {
				case glyph_outline::verb::MoveTo: appendPoints('M', 1); break;
				case glyph_outline::verb::LineTo: appendPoints('L', 1); break;
				case glyph_outline::verb::QuadTo: appendPoints('Q', 2); break;
				case glyph_outline::verb::CubicTo: appendPoints('C', 3); break;
				case glyph_outline::verb::Close: res += " Z"; break;
			}
		}
		return res;
	}

	// Pixels of a glyph drawn with its origin at (-x1, -y1) into a buffer of the given size, as coverage.
	std::vector<uint8_t> DrawCoverage(const xivres::fontgen::fixed_size_font& font, char32_t codepoint, int x1, int y1, int width, int height) {
		std::vector<uint8_t> coverage(static_cast<size_t>(width) * height);
		font.draw(codepoint, coverage.data(), 1, -x1, -y1, width, height, 255, 0, 255, 0);
		return coverage;
	}

	std::vector<xivres::util::b8g8r8a8> CoverageToBlackPixels(std::span<const uint8_t> coverage) {
		std::vector<xivres::util::b8g8r8a8> pixels(coverage.size(), xivres::util::b8g8r8a8(0u));
		for (size_t i = 0; i < coverage.size(); i++)
			pixels[i].A = coverage[i];
		return pixels;
	}

	// Finds the glyph merging font that draws the codepoint, and how far the glyph is moved from where that font puts it.
	const xivres::fontgen::glyph_merging_fixed_size_font* FindGlyphMergingFont(const xivres::fontgen::fixed_size_font& font, char32_t codepoint, int& dx, int& dy) {
		const auto base = dynamic_cast<const xivres::fontgen::glyph_merging_fixed_size_font*>(font.get_base_font(codepoint));
		if (!base)
			return nullptr;

		glyph_metrics gm, baseGm;
		if (!font.try_get_glyph_metrics(codepoint, gm) || !base->try_get_glyph_metrics(codepoint, baseGm))
			return nullptr;
		dx = gm.X1 - baseGm.X1;
		dy = gm.Y1 - baseGm.Y1;
		return base;
	}

	std::string MakeSvg(const xivres::fontgen::fixed_size_font& font, char32_t codepoint, const glyph_metrics& gm, const UnitMapping& m) {
		const auto lineHeight = font.line_height();
		const auto advance = m.X(gm.AdvanceX);
		const auto top = m.Y(0);
		const auto height = lineHeight * m.Scale;

		// A viewBox cannot be empty; glyphs that do not advance, such as combining marks, keep their advance in the attribute.
		const auto width = advance > 0 ? advance : (std::max)(1., m.X(gm.X2));

		std::string svg;
		svg += "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
		svg += std::format(
			"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\""
			" xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"\n"
			"     width=\"{0}\" height=\"{1}\" viewBox=\"0 {2} {0} {1}\"\n"
			"     data-xivfont-codepoint=\"{3}\" data-xivfont-units-per-em=\"{4}\" data-xivfont-baseline=\"{5}\" data-xivfont-advance=\"{6}\">\n",
			FormatNumber(width), FormatNumber(height), FormatNumber(top),
			FormatCodepoint(codepoint), FormatNumber(App::GlyphFiles::DefaultUnitsPerEm), FormatNumber(App::GlyphFiles::DefaultBaselineY), FormatNumber(advance));

		// Inkscape stores the positions of guides relative to the bottom left of the page, which is the viewBox here.
		const auto guide = [&](const char* id, const char* label, double x, double y, bool horizontal) {
			svg += std::format(
				"    <sodipodi:guide id=\"{}\" inkscape:label=\"{}\" position=\"{},{}\" orientation=\"{}\" inkscape:locked=\"true\" />\n",
				id, label, FormatNumber(x), FormatNumber(top + height - y), horizontal ? "0,1" : "1,0");
		};
		svg += "  <sodipodi:namedview id=\"namedview\" pagecolor=\"#ffffff\" inkscape:document-units=\"px\" showguides=\"true\">\n";
		guide("guide-ascent", "ascent", 0, top, true);
		guide("guide-baseline", "baseline", 0, App::GlyphFiles::DefaultBaselineY, true);
		guide("guide-descent", "descent", 0, top + height, true);
		guide("guide-origin", "origin", 0, top, false);
		guide("guide-advance", "advance", advance, top, false);
		svg += "  </sodipodi:namedview>\n";

		glyph_outline outline;
		if (font.try_get_glyph_outline(codepoint, outline)) {
			svg += "  <g id=\"glyph\" inkscape:groupmode=\"layer\" inkscape:label=\"Glyph\">\n";
			if (!outline.empty()) {
				svg += std::format(
					"    <path d=\"{}\"{} />\n",
					OutlineToPathData(outline, m),
					outline.EvenOdd ? " fill-rule=\"evenodd\"" : "");
			}
			svg += "  </g>\n";
		} else {
			// Glyphs without outlines are given as an image to trace, in a locked layer that is not drawn back.
			svg += "  <g id=\"xivfont-reference\" inkscape:groupmode=\"layer\" inkscape:label=\"Reference\" sodipodi:insensitive=\"true\" opacity=\"0.5\">\n";
			if (!gm.is_effectively_empty()) {
				const auto coverage = DrawCoverage(font, codepoint, gm.X1, gm.Y1, gm.width(), gm.height());
				const auto png = App::WicImage::EncodePng(gm.width(), gm.height(), CoverageToBlackPixels(coverage));
				svg += std::format(
					"    <image x=\"{}\" y=\"{}\" width=\"{}\" height=\"{}\" preserveAspectRatio=\"none\" style=\"image-rendering:pixelated\" xlink:href=\"data:image/png;base64,{}\" />\n",
					FormatNumber(m.X(gm.X1)), FormatNumber(m.Y(gm.Y1)), FormatNumber(gm.width() * m.Scale), FormatNumber(gm.height() * m.Scale),
					EncodeBase64(png));
			}

			// The shape of a merged glyph is in the units of shapes at the size and ascent of the element.
			int dx, dy;
			if (const auto merging = FindGlyphMergingFont(font, codepoint, dx, dy)) {
				if (const auto pathData = merging->get_shape_path_data(codepoint)) {
					const auto shapeScale = merging->font_size() / App::GlyphFiles::DefaultUnitsPerEm;
					const auto a = shapeScale * m.Scale;
					const auto e = m.X(dx);
					const auto f = m.Y(merging->ascent() + dy - App::GlyphFiles::DefaultBaselineY * shapeScale);
					svg += std::format(
						"    <path d=\"{}\" transform=\"matrix({} 0 0 {} {} {})\" fill=\"none\" stroke=\"#0080ff\" stroke-width=\"{}\" />\n",
						*pathData, FormatNumber(a), FormatNumber(a), FormatNumber(e), FormatNumber(f), FormatNumber(4 / a));
				}
			}
			svg += "  </g>\n";
			svg += "  <g id=\"glyph\" inkscape:groupmode=\"layer\" inkscape:label=\"Glyph\" />\n";
		}
		svg += "</svg>\n";
		return svg;
	}

	std::vector<uint8_t> DecodeBase64(std::string_view s) {
		std::vector<uint8_t> res;
		res.reserve(s.size() / 4 * 3);
		uint32_t acc = 0;
		int bits = 0;
		for (const auto c : s) {
			int v;
			if (c >= 'A' && c <= 'Z') v = c - 'A';
			else if (c >= 'a' && c <= 'z') v = c - 'a' + 26;
			else if (c >= '0' && c <= '9') v = c - '0' + 52;
			else if (c == '+') v = 62;
			else if (c == '/') v = 63;
			else continue;
			acc = acc << 6 | static_cast<uint32_t>(v);
			bits += 6;
			if (bits >= 8) {
				bits -= 8;
				res.push_back(static_cast<uint8_t>(acc >> bits & 0xFF));
			}
		}
		return res;
	}

	std::string ReadFileToString(const std::filesystem::path& path) {
		std::ifstream in(path, std::ios::binary);
		if (!in)
			throw std::runtime_error("Failed to open the file.");
		return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
	}

	xivres::fontgen::image_glyph_source MakeBitmapSource(std::string_view png) {
		xivres::fontgen::image_glyph_source source;
		auto pixels = App::WicImage::DecodeBgra({reinterpret_cast<const uint8_t*>(png.data()), png.size()}, source.Width, source.Height);
		source.Pixels = std::make_shared<const std::vector<uint32_t>>(std::move(pixels));
		return source;
	}

	std::optional<float> JsonNumber(const nlohmann::json& json, const char* key) {
		if (!json.is_object())
			return std::nullopt;
		if (const auto it = json.find(key); it != json.end() && it->is_number())
			return it->get<float>();
		return std::nullopt;
	}

	std::shared_ptr<const App::GlyphFiles::GlyphSet> ReadFolder(const std::filesystem::path& folder) {
		auto res = std::make_shared<App::GlyphFiles::GlyphSet>();
		std::error_code ec;
		if (!is_directory(folder, ec)) {
			res->Error = std::format(L"Folder not found: {}", folder.wstring());
			return res;
		}

		// SVG files are taken over PNG files of the same codepoint, and files without suffixes over ones with them.
		std::vector<std::filesystem::path> files;
		for (const auto& entry : std::filesystem::directory_iterator(folder, ec)) {
			if (entry.is_regular_file(ec))
				files.push_back(entry.path());
		}
		const auto rank = [](const std::filesystem::path& p) {
			auto ext = p.extension().wstring();
			CharLowerBuffW(ext.data(), static_cast<DWORD>(ext.size()));
			return std::make_tuple(ext == L".svg" ? 0 : 1, p.stem().wstring().find(L'_') != std::wstring::npos, p.filename().wstring());
		};
		std::ranges::sort(files, {}, rank);

		for (const auto& path : files) {
			auto ext = path.extension().wstring();
			CharLowerBuffW(ext.data(), static_cast<DWORD>(ext.size()));
			if (lstrcmpiW(path.filename().c_str(), App::GlyphFiles::MetadataFileName) == 0) {
				try {
					res->Metadata = nlohmann::json::parse(ReadFileToString(path));
				} catch (const std::exception&) {
					res->SkippedCount++;
				}
				continue;
			}
			if (ext != L".svg" && ext != L".png")
				continue;

			// Files of codepoints already taken, such as the PNG files that are exported beside SVG files, are not counted.
			const auto codepoint = App::GlyphFiles::ParseGlyphFileName(path);
			if (!codepoint) {
				res->SkippedCount++;
				continue;
			}
			if (res->Glyphs.contains(*codepoint))
				continue;

			try {
				auto data = ReadFileToString(path);
				if (ext == L".svg") {
					xivres::fontgen::image_fixed_size_font::svg_metrics metrics;
					if (!xivres::fontgen::image_fixed_size_font::try_read_svg_metrics(data, metrics))
						throw std::runtime_error("Not an SVG document");
					res->Glyphs[*codepoint].Svg = std::make_shared<const std::string>(std::move(data));
				} else {
					res->Glyphs[*codepoint] = MakeBitmapSource(data);
					res->PngFiles[*codepoint] = std::make_shared<const std::string>(std::move(data));
				}
			} catch (const std::exception&) {
				res->SkippedCount++;
			}
		}
		return res;
	}

	std::mutex s_folderCacheMtx;
	std::map<std::filesystem::path, std::shared_ptr<const App::GlyphFiles::GlyphSet>> s_folderCache;

	// Watches folders on a thread of its own, and tells a window about changes once the files have been quiet for a
	// moment, as editors often save through temporary files.
	class FolderWatcher {
		static constexpr DWORD QuietMilliseconds = 300;

		std::mutex m_mtx;
		HWND m_hWnd = nullptr;
		UINT m_message = 0;
		std::set<std::filesystem::path> m_folders;
		std::vector<std::filesystem::path> m_changed;
		HANDLE m_wakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
		std::thread m_thread;

		void Run() {
			std::map<std::filesystem::path, HANDLE> handles;
			std::map<std::filesystem::path, ULONGLONG> pending;
			while (true) {
				{
					const auto lock = std::lock_guard(m_mtx);
					if (!m_hWnd)
						break;
					for (const auto& folder : m_folders) {
						if (handles.contains(folder))
							continue;
						const auto h = FindFirstChangeNotificationW(folder.c_str(), FALSE, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE);
						handles.emplace(folder, h);
					}
				}

				std::vector<HANDLE> waitHandles{m_wakeEvent};
				std::vector<std::filesystem::path> waitFolders{{}};
				for (const auto& [folder, h] : handles) {
					if (h != INVALID_HANDLE_VALUE && waitHandles.size() < MAXIMUM_WAIT_OBJECTS) {
						waitHandles.push_back(h);
						waitFolders.push_back(folder);
					}
				}

				auto timeout = INFINITE;
				const auto now = GetTickCount64();
				for (const auto& [_, since] : pending)
					timeout = (std::min<DWORD>)(timeout, static_cast<DWORD>(since + QuietMilliseconds > now ? since + QuietMilliseconds - now : 0));

				const auto r = WaitForMultipleObjects(static_cast<DWORD>(waitHandles.size()), waitHandles.data(), FALSE, timeout);
				if (r > WAIT_OBJECT_0 && r < WAIT_OBJECT_0 + waitHandles.size()) {
					const auto i = r - WAIT_OBJECT_0;
					pending[waitFolders[i]] = GetTickCount64();
					FindNextChangeNotification(waitHandles[i]);
				}

				std::vector<std::filesystem::path> quiet;
				for (auto it = pending.begin(); it != pending.end();) {
					if (GetTickCount64() >= it->second + QuietMilliseconds) {
						quiet.push_back(it->first);
						it = pending.erase(it);
					} else {
						++it;
					}
				}
				if (!quiet.empty()) {
					for (const auto& folder : quiet)
						App::GlyphFiles::InvalidateFolder(folder);

					const auto lock = std::lock_guard(m_mtx);
					m_changed.insert(m_changed.end(), quiet.begin(), quiet.end());
					if (m_hWnd)
						PostMessageW(m_hWnd, m_message, 0, 0);
				}
			}

			for (const auto& [_, h] : handles) {
				if (h != INVALID_HANDLE_VALUE)
					FindCloseChangeNotification(h);
			}
		}

	public:
		~FolderWatcher() {
			{
				const auto lock = std::lock_guard(m_mtx);
				m_hWnd = nullptr;
			}
			SetEvent(m_wakeEvent);
			if (m_thread.joinable())
				m_thread.join();
			CloseHandle(m_wakeEvent);
		}

		static FolderWatcher& Instance() {
			static FolderWatcher s_instance;
			return s_instance;
		}

		void SetWindow(HWND hWnd, UINT message) {
			{
				const auto lock = std::lock_guard(m_mtx);
				m_hWnd = hWnd;
				m_message = message;
			}
			SetEvent(m_wakeEvent);
			if (hWnd && !m_thread.joinable())
				m_thread = std::thread([this] { Run(); });
			else if (!hWnd && m_thread.joinable())
				m_thread.join();
		}

		void Watch(const std::filesystem::path& folder) {
			{
				const auto lock = std::lock_guard(m_mtx);
				if (!m_hWnd || !m_folders.insert(folder).second)
					return;
			}
			SetEvent(m_wakeEvent);
		}

		std::vector<std::filesystem::path> TakeChanged() {
			const auto lock = std::lock_guard(m_mtx);
			return std::exchange(m_changed, {});
		}
	};

	void WriteFile(const std::filesystem::path& path, std::string_view data) {
		std::ofstream out(path, std::ios::binary);
		if (!out)
			throw std::runtime_error(std::format("Failed to open {} for writing.", xivres::util::unicode::convert<std::string>(path.wstring())));
		out.write(data.data(), static_cast<std::streamsize>(data.size()));
		if (!out)
			throw std::runtime_error(std::format("Failed to write to {}.", xivres::util::unicode::convert<std::string>(path.wstring())));
	}
}

std::wstring App::GlyphFiles::GetGlyphFileStem(char32_t codepoint) {
	if (codepoint < 0x10000)
		return std::format(L"uni{:04X}", static_cast<uint32_t>(codepoint));
	return std::format(L"u{:05X}", static_cast<uint32_t>(codepoint));
}

std::optional<char32_t> App::GlyphFiles::ParseGlyphFileName(const std::filesystem::path& path) {
	auto stem = path.stem().wstring();
	CharLowerBuffW(stem.data(), static_cast<DWORD>(stem.size()));
	if (const auto suffix = stem.find(L'_'); suffix != std::wstring::npos)
		stem.resize(suffix);

	std::wstring_view hex(stem);
	if (hex.starts_with(L"uni"))
		hex.remove_prefix(3);
	else if (hex.starts_with(L"u+"))
		hex.remove_prefix(2);
	else if (hex.starts_with(L"u"))
		hex.remove_prefix(1);
	else
		return std::nullopt;

	if (hex.size() < 4 || hex.size() > 6 || !std::ranges::all_of(hex, [](wchar_t c) { return iswxdigit(c) != 0; }))
		return std::nullopt;

	const auto value = std::wcstoul(std::wstring(hex).c_str(), nullptr, 16);
	if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
		return std::nullopt;
	return static_cast<char32_t>(value);
}

std::filesystem::path App::GlyphFiles::ResolvePath(const std::string& path) {
	auto p = std::filesystem::path(xivres::util::unicode::convert<std::wstring>(path));
	if (p.is_relative()) {
		if (const auto base = Structs::GetProjectDirectory(); !base.empty())
			p = base / p;
	}
	return p.lexically_normal();
}

std::shared_ptr<const App::GlyphFiles::GlyphSet> App::GlyphFiles::LoadGlyphSet(const Structs::GlyphImagesStruct& settings) {
	if (settings.IsEmbedded()) {
		auto res = std::make_shared<GlyphSet>();
		res->Metadata = settings.EmbeddedMetadata.value_or(nlohmann::json());
		for (const auto& [codepoint, glyph] : settings.Embedded) {
			try {
				if (glyph.Svg) {
					res->Glyphs[codepoint].Svg = glyph.Svg;
				} else if (glyph.PngBase64) {
					const auto png = DecodeBase64(*glyph.PngBase64);
					res->Glyphs[codepoint] = MakeBitmapSource({reinterpret_cast<const char*>(png.data()), png.size()});
				}
			} catch (const std::exception&) {
				res->SkippedCount++;
			}
		}
		return res;
	}

	if (settings.Path.empty()) {
		auto res = std::make_shared<GlyphSet>();
		res->Error = L"No folder";
		return res;
	}

	const auto folder = ResolvePath(settings.Path);
	FolderWatcher::Instance().Watch(folder);
	{
		const auto lock = std::lock_guard(s_folderCacheMtx);
		if (const auto it = s_folderCache.find(folder); it != s_folderCache.end())
			return it->second;
	}

	auto res = ReadFolder(folder);
	const auto lock = std::lock_guard(s_folderCacheMtx);
	return s_folderCache.insert_or_assign(folder, std::move(res)).first->second;
}

void App::GlyphFiles::InvalidateFolder(const std::filesystem::path& folder) {
	const auto lock = std::lock_guard(s_folderCacheMtx);
	s_folderCache.erase(folder.lexically_normal());
}

void App::GlyphFiles::SetChangeNotificationWindow(HWND hWnd, UINT message) {
	FolderWatcher::Instance().SetWindow(hWnd, message);
}

std::vector<std::filesystem::path> App::GlyphFiles::TakeChangedFolders() {
	return FolderWatcher::Instance().TakeChanged();
}

std::shared_ptr<xivres::fontgen::fixed_size_font> App::GlyphFiles::CreateFont(
	const Structs::GlyphImagesStruct& settings,
	float size,
	float gamma,
	const xivres::fontgen::font_render_transformation_matrix& matrix) {
	const auto set = LoadGlyphSet(settings);
	const auto& metadata = set->Metadata;

	xivres::fontgen::image_fixed_size_font::create_struct params;
	params.Glyphs = set->Glyphs;
	params.BitmapCoverage = settings.BitmapCoverage;
	params.FamilyName = settings.IsEmbedded()
		? "(Embedded)"
		: xivres::util::unicode::convert<std::string>(ResolvePath(settings.Path).filename().wstring());

	// The element overrides font.json, which tells the units of the files that have no attributes for them.
	const auto metadataUnitsPerEm = JsonNumber(metadata, "unitsPerEm").value_or(DefaultUnitsPerEm);
	params.UnitsPerEm = settings.UnitsPerEm.value_or(metadataUnitsPerEm);
	params.BaselineY = settings.BaselineY.value_or(JsonNumber(metadata, "baselineY").value_or(DefaultBaselineY));
	params.UnitsPerEmOverridesAttribute = settings.UnitsPerEm.has_value();
	params.BaselineYOverridesAttribute = settings.BaselineY.has_value();

	const auto fromMetadataUnits = params.UnitsPerEm / metadataUnitsPerEm;
	if (settings.Ascent)
		params.Ascent = *settings.Ascent;
	else if (const auto v = JsonNumber(metadata, "ascent"))
		params.Ascent = *v * fromMetadataUnits;
	else
		params.Ascent = params.BaselineY;
	if (settings.LineHeight)
		params.LineHeight = *settings.LineHeight;
	else if (const auto v = JsonNumber(metadata, "lineHeight"))
		params.LineHeight = *v * fromMetadataUnits;
	else
		params.LineHeight = params.UnitsPerEm;

	if (const auto it = metadata.is_object() ? metadata.find("kerning") : metadata.end(); metadata.is_object() && it != metadata.end() && it->is_array()) {
		for (const auto& entry : *it) {
			if (entry.is_array() && entry.size() == 3 && entry[0].is_number_unsigned() && entry[1].is_number_unsigned() && entry[2].is_number())
				params.KerningPairs[{entry[0].get<char32_t>(), entry[1].get<char32_t>()}] = entry[2].get<float>() * fromMetadataUnits;
		}
	}

	// PNG files are in pixels at the size that they were exported at, with the baseline at the ascent; without font.json,
	// they are drawn as they are at any size.
	params.BitmapUnitsPerEm = settings.UnitsPerEm.value_or(JsonNumber(metadata, "exportedSize").value_or(size));
	params.BitmapBaselineY = settings.BaselineY.value_or(JsonNumber(metadata, "ascentPx").value_or(std::round(params.BitmapUnitsPerEm * DefaultBaselineY / DefaultUnitsPerEm)));
	if (const auto it = metadata.is_object() ? metadata.find("glyphs") : metadata.end(); metadata.is_object() && it != metadata.end() && it->is_object()) {
		for (auto& [codepoint, source] : params.Glyphs) {
			if (!source.Pixels)
				continue;
			const auto entry = it->find(FormatCodepoint(codepoint));
			if (entry == it->end() || !entry->is_object())
				continue;
			source.Advance = JsonNumber(*entry, "advancePx");
			if (const auto origin = entry->find("pngOrigin"); origin != entry->end() && origin->is_array() && origin->size() == 2 && (*origin)[0].is_number() && (*origin)[1].is_number()) {
				source.OriginX = (*origin)[0].get<int>();
				source.OriginY = (*origin)[1].get<int>();
			}
		}
	}

	return std::make_shared<xivres::fontgen::image_fixed_size_font>(std::move(params), size, gamma, matrix);
}

void App::GlyphFiles::Embed(Structs::GlyphImagesStruct& settings) {
	if (settings.IsEmbedded())
		return;

	const auto set = LoadGlyphSet(settings);
	if (!set->Error.empty())
		throw std::runtime_error(xivres::util::unicode::convert<std::string>(set->Error));

	std::map<char32_t, Structs::GlyphImagesStruct::EmbeddedGlyph> embedded;
	for (const auto& [codepoint, source] : set->Glyphs) {
		if (source.Svg)
			embedded[codepoint].Svg = source.Svg;
		else if (const auto it = set->PngFiles.find(codepoint); it != set->PngFiles.end())
			embedded[codepoint].PngBase64 = std::make_shared<const std::string>(EncodeBase64({reinterpret_cast<const uint8_t*>(it->second->data()), it->second->size()}));
	}
	if (embedded.empty())
		throw std::runtime_error("The folder has no glyph files.");

	settings.Embedded = std::move(embedded);
	if (!set->Metadata.is_null())
		settings.EmbeddedMetadata = set->Metadata;
	else
		settings.EmbeddedMetadata.reset();
}

std::shared_ptr<xivres::fontgen::fixed_size_font> App::GlyphFiles::GetElementFontForExport(const Structs::FaceElement& element, bool withAdjustments) {
	if (withAdjustments)
		return element.GetWrappedFont();

	// The codepoints and their replacements are kept, as they decide which glyphs the element adds.
	auto modifiers = element.WrapModifiers;
	modifiers.LetterSpacing = modifiers.HorizontalOffset = modifiers.BaselineShift = 0;
	return std::make_shared<xivres::fontgen::wrapping_fixed_size_font>(element.GetBaseFont(), modifiers);
}

nlohmann::json App::GlyphFiles::DescribeSource(const Structs::FaceElement& element) {
	const auto& font = *element.GetBaseFont();
	return {
		{"family", font.family_name()},
		{"subfamily", font.subfamily_name()},
		{"renderer", xivres::util::unicode::convert<std::string>(element.GetRendererRepresentation())},
		{"element", element},
	};
}

nlohmann::json App::GlyphFiles::DescribeSource(const Structs::Face& face) {
	return {
		{"face", face.Name},
	};
}

void App::GlyphFiles::ExportGlyphs(
	const xivres::fontgen::fixed_size_font& font,
	const std::filesystem::path& folder,
	const ExportOptions& options,
	const std::function<void(size_t, size_t)>& progress) {
	create_directories(folder);

	const auto size = font.font_size();
	if (!(size > 0))
		throw std::invalid_argument("The font has no size.");

	// Pixel exact: units are taken so that the metrics, which are in whole pixels, round back to the same values when
	// the files are drawn at the same size.
	const UnitMapping m{DefaultUnitsPerEm / size, font.ascent()};
	const auto lineHeight = font.line_height();

	auto glyphs = nlohmann::json::object();
	const std::vector<char32_t> codepoints(font.all_codepoints().begin(), font.all_codepoints().end());
	for (size_t i = 0; i < codepoints.size(); i++) {
		if (progress)
			progress(i, codepoints.size());

		const auto codepoint = codepoints[i];
		glyph_metrics gm;
		if (!font.try_get_glyph_metrics(codepoint, gm))
			continue;

		const auto stem = GetGlyphFileStem(codepoint);
		if (options.Svg)
			WriteFile(folder / (stem + L".svg"), MakeSvg(font, codepoint, gm, m));

		if (options.Png) {
			// The image covers the line box, and grows to keep the pixels that are outside of it.
			const auto x1 = (std::min)(0, gm.is_effectively_empty() ? 0 : gm.X1);
			const auto y1 = (std::min)(0, gm.is_effectively_empty() ? 0 : gm.Y1);
			const auto x2 = (std::max)({x1 + 1, gm.AdvanceX, gm.is_effectively_empty() ? 0 : gm.X2});
			const auto y2 = (std::max)({y1 + 1, lineHeight, gm.is_effectively_empty() ? 0 : gm.Y2});
			const auto coverage = DrawCoverage(font, codepoint, x1, y1, x2 - x1, y2 - y1);
			WicImage::SavePng(x2 - x1, y2 - y1, CoverageToBlackPixels(coverage), folder / (stem + L".png"));

			if (x1 != 0 || y1 != 0 || x2 != gm.AdvanceX) {
				glyphs[FormatCodepoint(codepoint)] = {
					{"advancePx", gm.AdvanceX},
					{"pngOrigin", nlohmann::json::array({-x1, -y1})},
				};
			}
		}
	}

	auto kerning = nlohmann::json::array();
	for (const auto& [pair, value] : font.all_kerning_pairs()) {
		if (value)
			kerning.push_back(nlohmann::json::array({static_cast<uint32_t>(pair.first), static_cast<uint32_t>(pair.second), std::stod(FormatNumber(value * m.Scale))}));
	}

	nlohmann::json metadata{
		{"generator", "XivRes.FontGenerator"},
		{"unitsPerEm", DefaultUnitsPerEm},
		{"baselineY", DefaultBaselineY},
		{"ascent", std::stod(FormatNumber(font.ascent() * m.Scale))},
		{"lineHeight", std::stod(FormatNumber(lineHeight * m.Scale))},
		{"exportedSize", size},
		{"ascentPx", font.ascent()},
		{"lineHeightPx", lineHeight},
		{"pixelExact", true},
		{"withAdjustments", options.WithAdjustments},
		{"source", options.Source},
		{"kerning", std::move(kerning)},
	};
	if (!glyphs.empty())
		metadata["glyphs"] = std::move(glyphs);
	WriteFile(folder / MetadataFileName, metadata.dump(1, '\t'));

	if (progress)
		progress(codepoints.size(), codepoints.size());
}
