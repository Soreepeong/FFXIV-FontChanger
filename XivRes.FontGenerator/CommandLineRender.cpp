#include "pch.h"
#include "CommandLineRender.h"

#include <wincodec.h>

#include "Structs.h"

#pragma comment(lib, "windowscodecs.lib")

_COM_SMARTPTR_TYPEDEF(IWICImagingFactory, __uuidof(IWICImagingFactory));
_COM_SMARTPTR_TYPEDEF(IWICStream, __uuidof(IWICStream));
_COM_SMARTPTR_TYPEDEF(IWICBitmapEncoder, __uuidof(IWICBitmapEncoder));
_COM_SMARTPTR_TYPEDEF(IWICBitmapFrameEncode, __uuidof(IWICBitmapFrameEncode));

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

	void SavePng(const xivres::texture::memory_mipmap_stream& mipmap, const std::filesystem::path& path) {
		IWICImagingFactoryPtr factory;
		SuccessOrThrow(factory.CreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER));

		IWICStreamPtr stream;
		SuccessOrThrow(factory->CreateStream(&stream));
		SuccessOrThrow(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));

		IWICBitmapEncoderPtr encoder;
		SuccessOrThrow(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder));
		SuccessOrThrow(encoder->Initialize(stream, WICBitmapEncoderNoCache));

		IWICBitmapFrameEncodePtr frame;
		SuccessOrThrow(encoder->CreateNewFrame(&frame, nullptr));
		SuccessOrThrow(frame->Initialize(nullptr));
		SuccessOrThrow(frame->SetSize(static_cast<UINT>(mipmap.Width), static_cast<UINT>(mipmap.Height)));

		auto format = GUID_WICPixelFormat32bppBGRA;
		SuccessOrThrow(frame->SetPixelFormat(&format));
		if (format != GUID_WICPixelFormat32bppBGRA)
			throw std::runtime_error("The PNG encoder does not take 32-bit BGRA pixels.");

		const auto pixels = mipmap.as_span<xivres::util::b8g8r8a8>();
		const auto stride = static_cast<UINT>(mipmap.Width * sizeof(xivres::util::b8g8r8a8));
		SuccessOrThrow(frame->WritePixels(
			static_cast<UINT>(mipmap.Height),
			stride,
			static_cast<UINT>(pixels.size_bytes()),
			const_cast<BYTE*>(reinterpret_cast<const BYTE*>(pixels.data()))));
		SuccessOrThrow(frame->Commit());
		SuccessOrThrow(encoder->Commit());
	}

	int Render(const std::vector<std::wstring>& args) {
		std::optional<std::wstring> text, output, fontName;
		std::optional<int> maxWidth;
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
			else
				throw std::invalid_argument(std::format("Unknown argument: {}", xivres::util::unicode::convert<std::string>(args[i])));
		}
		if (args.size() < 2 || args[1].starts_with(L"--"))
			throw std::invalid_argument("The first argument must be the path of a configuration file.");
		if (!output)
			throw std::invalid_argument("--output is required.");

		std::ifstream in(std::filesystem::path(args[1]), std::ios::binary);
		if (!in)
			throw std::runtime_error(std::format("Failed to open {}.", xivres::util::unicode::convert<std::string>(args[1])));
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

		const auto& font = *face->GetMergedFont();
		const auto measured = xivres::fontgen::text_measurer(font)
			.max_width(maxWidth.value_or((std::numeric_limits<int>::max)()))
			.use_kerning(true)
			.measure(xivres::util::unicode::convert<std::string>(*text));

		// White text on black, as in the preview.
		const auto mipmap = measured.create_mipmap(font, {0xFF, 0xFF, 0xFF, 0xFF}, {0x00, 0x00, 0x00, 0xFF}, 16);
		SavePng(*mipmap, *output);
		return 0;
	}
}

std::optional<int> App::RunCommandLineRender(const std::vector<std::wstring>& args) {
	if (std::ranges::find(args, L"--render-text") == args.end())
		return std::nullopt;

	try {
		return Render(args);
	} catch (const _com_error& e) {
		WriteError(std::format(L"Error: {}", e.ErrorMessage()));
	} catch (const std::exception& e) {
		WriteError(std::format(L"Error: {}", xivres::util::unicode::convert<std::wstring>(e.what())));
	}
	return 1;
}
