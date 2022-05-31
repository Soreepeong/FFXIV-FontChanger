#include "pch.h"
#include "resource.h"

#include "ExportPreviewWindow.h"
#include "FaceElementEditorDialog.h"
#include "Structs.h"
#include "MainWindow.h"

#pragma comment(lib, "Comctl32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' \
name='Microsoft.Windows.Common-Controls' version='6.0.0.0' \
processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

const char PatternText[] = "\x40\x56\x41\x54\x41\x55\x41\x57\x48\x81\xec\x00\x00\x00\x00\x48\x8b\x05\x00\x00\x00\x00\x48\x33\xc4\x48\x89\x84\x24\x00\x00\x00\x00\x44\x88\x44\x24\x00\x45\x0f\xb6\xf8";
const char PatternMask[] = "\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF\x00\xFF\xFF\xFF\xFF";
const char Pattern2Text[] = "\x48\x8D\x05\x00\x00\x00\x00";
const char Pattern2Mask[] = "\xFB\xFF\xDF\x00\x00\x00\x00";
HINSTANCE g_hInstance;

std::span<char> LookupForData(std::span<char> range, std::span<const char> pattern, std::span<const char> mask) {
	for (size_t i = 0, i_ = range.size() - pattern.size(); i < i_; ++i) {
		auto equals = true;
		for (size_t j = 0; equals && j < pattern.size(); ++j) {
			if ((range[i + j] & mask[j]) != (pattern[j] & mask[j])) {
				equals = false;
			}
		}
		if (equals)
			return { &range[i], pattern.size() };
	}

	return {};
}

struct FontSet {
	uint64_t VirtualAddress;

	struct Face {
		int TexCount;
		std::string TexPattern;
		std::string Fdt;
	} Faces[0x29];
};

std::vector<FontSet> ReadFontSets(std::filesystem::path path) {
	std::shared_ptr<void> hFile(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr), &CloseHandle);
	std::vector<char> buf(GetFileSize(hFile.get(), nullptr));
	DWORD rd;
	ReadFile(hFile.get(), &buf[0], static_cast<DWORD>(buf.size()), &rd, nullptr);
	if (rd != buf.size())
		return {};

	std::vector<FontSet> res;

	struct FontSetInGame {
		struct Face {
			int TexCount;
			int Padding;
			char* TexPattern;
			char* Fdt;
		} Faces[0x29];
	};

	const auto& dosHeader = *reinterpret_cast<IMAGE_DOS_HEADER*>(&buf[0]);
	const auto& ntHeader64 = *reinterpret_cast<IMAGE_NT_HEADERS64*>(&buf[dosHeader.e_lfanew]);
	const auto sectionHeaders = std::span(IMAGE_FIRST_SECTION(&ntHeader64), ntHeader64.FileHeader.NumberOfSections);
	const auto& rva2sec = [&](size_t rva) -> IMAGE_SECTION_HEADER& {
		for (auto& sectionHeader2 : sectionHeaders) {
			if (sectionHeader2.VirtualAddress <= rva && rva < sectionHeader2.VirtualAddress + sectionHeader2.Misc.VirtualSize)
				return sectionHeader2;
		}
		throw std::runtime_error("rva");
	};
	const auto& va2rva = [&](void* va) {
		return reinterpret_cast<uint64_t>(va) - ntHeader64.OptionalHeader.ImageBase;
	};
	const auto& va2sec = [&](void* va) -> IMAGE_SECTION_HEADER& {
		return rva2sec(reinterpret_cast<uint64_t>(va) - ntHeader64.OptionalHeader.ImageBase);
	};
	for (const auto& sectionHeader : sectionHeaders) {
		const auto section = std::span(&buf[sectionHeader.PointerToRawData], sectionHeader.SizeOfRawData);
		if (strncmp(reinterpret_cast<const char*>(sectionHeader.Name), ".text", IMAGE_SIZEOF_SHORT_NAME) != 0)
			continue;

		const auto ptr = LookupForData(section, PatternText, PatternMask);
		if (ptr.empty())
			continue;

		for (std::span<char> range2 = std::span(&ptr.back() + 1, 128), f; !(f = LookupForData(range2, Pattern2Mask, Pattern2Text)).empty(); range2 = range2.subspan(&f[1] - &range2[0])) {
			auto rva = &f.back() + *reinterpret_cast<int*>(&f[3]) - &buf[0];
			rva += sectionHeader.VirtualAddress - sectionHeader.PointerToRawData;

			const auto& fontSetSection = rva2sec(rva);
			FontSetInGame fontSet = *reinterpret_cast<FontSetInGame*>(&buf[rva - fontSetSection.VirtualAddress + fontSetSection.PointerToRawData]);

			res.emplace_back();
			res.back().VirtualAddress = rva;

			for (size_t i = 0; i < 0x29; i++) {
				auto& sourceItem = fontSet.Faces[i];
				auto& targetItem = res.back().Faces[i];
				const auto& texPatternSection = va2sec(sourceItem.TexPattern);
				const auto& fdtSection = va2sec(sourceItem.Fdt);
				targetItem.TexPattern = &buf[va2rva(sourceItem.TexPattern) + texPatternSection.PointerToRawData - texPatternSection.VirtualAddress];
				targetItem.Fdt = &buf[va2rva(sourceItem.Fdt) + fdtSection.PointerToRawData - fdtSection.VirtualAddress];
				targetItem.TexCount = sourceItem.TexCount;
			}
		}
	}

	return res;
}

int __stdcall WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int nShowCmd) {
	g_hInstance = hInstance;

	//std::vector<std::vector<FontSet>> sets;
	//sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe)"));
	//sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\SNDA\FFXIV\game\ffxiv_dx11.exe)"));
	//sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game\ffxiv_dx11.exe)"));

	//{
	//	std::ofstream test("Z:/fonts.csv");
	//	for (const auto& set : sets[0])
	//		test << std::format("g,0x{:X},,", set.VirtualAddress);
	//	for (const auto& set : sets[1])
	//		test << std::format("c,0x{:X},,", set.VirtualAddress);
	//	for (const auto& set : sets[2])
	//		test << std::format("k,0x{:X},,", set.VirtualAddress);
	//	test << std::endl;
	//	for (int i = 0; i < 0x29; i++) {
	//		for (const auto& sets2 : sets) {
	//			for (const auto& set : sets2)
	//				test << std::format("{},{},{},", set.Faces[i].TexCount, set.Faces[i].TexPattern, set.Faces[i].Fdt);
	//		}
	//		test << std::endl;
	//	}
	//}

	std::vector<std::wstring> args;
	if (int nArgs{}; LPWSTR * szArgList = CommandLineToArgvW(GetCommandLineW(), &nArgs)) {
		if (szArgList) {
			for (int i = 0; i < nArgs; i++)
				args.emplace_back(szArgList[i]);
			LocalFree(szArgList);
		}
	}

	App::FontEditorWindow window(std::move(args));
	for (MSG msg{}; GetMessageW(&msg, nullptr, 0, 0);) {
		if (App::BaseWindow::ConsumeMessage(msg))
			continue;

		TranslateMessage(&msg);
		DispatchMessageW(&msg);
	}

	return 0;
}
