#include "pch.h"
#include "CommandLineRender.h"

#include "FontChanger.Presets/GlyphFiles.h"
#include "FontChanger.Presets/Structs.h"
#include "FontChanger.Presets/WicImage.h"

namespace {
	// The app has no console of its own; messages go to the console that started it, if any.
	void WriteError(const std::wstring& message) {
		if (!AttachConsole(ATTACH_PARENT_PROCESS) && GetLastError() != ERROR_ACCESS_DENIED)
			return;

		const auto handle = GetStdHandle(STD_ERROR_HANDLE);
		if (!handle || handle == INVALID_HANDLE_VALUE)
			return;

		const auto line = message + L"\r\n";
		if (DWORD written; !WriteConsoleW(handle, line.data(), static_cast<DWORD>(line.size()), &written, nullptr)) {
			// Redirected to a file or a pipe.
			const auto utf8 = xivres::util::unicode::convert<std::string>(line);
			WriteFile(handle, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
		}
	}

	std::wstring UnescapeText(std::wstring_view text) {
		std::wstring res;
		for (size_t i = 0; i < text.size(); i++) {
			if (text[i] == L'\\' && i + 1 < text.size() && (text[i + 1] == L'n' || text[i + 1] == L'\\')) {
				res += text[i + 1] == L'n' ? L'\n' : L'\\';
				i++;
			} else {
				res += text[i];
			}
		}
		return res;
	}

	int Render(const std::vector<std::wstring>& args) {
		std::optional<std::wstring> text, output, fontName, exportFolder;
		std::optional<int> maxWidth;
		std::optional<size_t> elementIndex;
		auto withAdjustments = false, exportSvg = true, exportPng = true;
		for (size_t i = 2; i < args.size(); i++) {
			const auto takeValue = [&]() -> const std::wstring& {
				if (i + 1 >= args.size())
					throw std::invalid_argument(std::format("{} needs a value.", xivres::util::unicode::convert<std::string>(args[i])));
				return args[++i];
			};
			if (args[i] == L"--render-text")
				text = UnescapeText(takeValue());
			else if (args[i] == L"--output")
				output = takeValue();
			else if (args[i] == L"--font")
				fontName = takeValue();
			else if (args[i] == L"--max-width")
				maxWidth = std::stoi(takeValue());
			else if (args[i] == L"--export-glyphs")
				exportFolder = takeValue();
			else if (args[i] == L"--element")
				elementIndex = std::stoul(takeValue());
			else if (args[i] == L"--with-adjustments")
				withAdjustments = true;
			else if (args[i] == L"--no-svg")
				exportSvg = false;
			else if (args[i] == L"--no-png")
				exportPng = false;
			else
				throw std::invalid_argument(std::format("Unknown argument: {}", xivres::util::unicode::convert<std::string>(args[i])));
		}
		if (args.size() < 2 || args[1].starts_with(L"--"))
			throw std::invalid_argument("The first argument must be the path of a configuration file.");
		if (!output && !exportFolder)
			throw std::invalid_argument("--output is required.");

		const auto configPath = std::filesystem::absolute(std::filesystem::path(args[1]));
		std::ifstream in(configPath, std::ios::binary);
		if (!in)
			throw std::runtime_error(std::format("Failed to open {}.", xivres::util::unicode::convert<std::string>(args[1])));
		App::Structs::SetProjectDirectory(configPath.parent_path());
		const auto multiFontSet = nlohmann::json::parse(in).get<App::Structs::MultiFontSet>();

		const App::Structs::Face* face = nullptr;
		for (const auto& fontSet : multiFontSet.FontSets) {
			for (const auto& f : fontSet->Faces) {
				if (!face && (!fontName || xivres::util::unicode::convert<std::wstring>(f->Name) == *fontName))
					face = f.get();
			}
		}
		if (!face) {
			throw std::invalid_argument(fontName
				? std::format("No font is named {}.", xivres::util::unicode::convert<std::string>(*fontName))
				: std::string("The configuration has no font."));
		}

		if (exportFolder) {
			const App::Structs::FaceElement* element = nullptr;
			if (elementIndex) {
				if (*elementIndex >= face->Elements.size())
					throw std::invalid_argument(std::format("The font has {} elements.", face->Elements.size()));
				element = face->Elements[*elementIndex].get();
			}
			const auto font = element ? App::GlyphFiles::GetElementFontForExport(*element, withAdjustments) : face->GetMergedFont();
			App::GlyphFiles::ExportGlyphs(*font, *exportFolder, {
				.Svg = exportSvg,
				.Png = exportPng,
				.WithAdjustments = withAdjustments || !element,
				.Source = element ? App::GlyphFiles::DescribeSource(*element) : App::GlyphFiles::DescribeSource(*face),
			});
			if (!output)
				return 0;
		}
		if (!text)
			throw std::invalid_argument("--render-text is required.");

		const auto& font = *face->GetMergedFont();
		const auto measured = xivres::fontgen::text_measurer(font)
			.max_width(maxWidth.value_or((std::numeric_limits<int>::max)()))
			.use_kerning(true)
			.measure(xivres::util::unicode::convert<std::string>(*text));

		// White text on black, as in the preview.
		const auto mipmap = measured.create_mipmap(font, {0xFF, 0xFF, 0xFF, 0xFF}, {0x00, 0x00, 0x00, 0xFF}, 16);
		App::WicImage::SavePng(*mipmap, *output);
		return 0;
	}
}

std::optional<int> App::RunCommandLineRender(const std::vector<std::wstring>& args) {
	if (std::ranges::find(args, L"--render-text") == args.end() && std::ranges::find(args, L"--export-glyphs") == args.end())
		return std::nullopt;

	// Nobody may be there to dismiss a dialog; elements whose game installation is missing are drawn empty.
	App::Structs::SetGameFontErrorHandler(nullptr);

	try {
		return Render(args);
	} catch (const _com_error& e) {
		WriteError(std::format(L"Error: {}", e.ErrorMessage()));
	} catch (const std::exception& e) {
		WriteError(std::format(L"Error: {}", xivres::util::unicode::convert<std::wstring>(e.what())));
	}
	return 1;
}
