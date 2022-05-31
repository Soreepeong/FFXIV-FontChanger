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

const char Framework_GetUiModulePatternText[] = "\x48\x8B\x0D\x00\x00\x00\x00" "\x4C\x89\xA4\x24\x00\x00\x00\x00" "\xE8\x00\x00\x00\x00\x80\x7B\x1D\x01";
const char Framework_GetUiModulePatternMask[] = "\xFF\xFF\xFF\x00\x00\x00\x00" "\xFF\xFF\xFF\xFF\x00\x00\x00\x00" "\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF";
const size_t Framework_GetUiModulePattern_FrameworkOffsetOffset = 3;
const size_t Framework_GetUiModulePattern_GetUiModuleOffsetOffset = 16;

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

using AtkModule_Vf43 = void(void* pAtkModule, bool isLobby, bool forceReload);

struct GameProcess {
	HANDLE hProcess;
	HMODULE hModule;

	struct FontSet {
		uint64_t VirtualAddress;

		struct Face {
			int TexCount;
			std::string TexPattern;
			std::string Fdt;
		} Faces[0x29];
	};

	std::vector<FontSet> FontSets;

	void* pAtkModule;
	AtkModule_Vf43* pfnRefreshFonts;

	void RefreshFonts() const {
		struct EnumWindowsStruct {
			const GameProcess& Process;
			HWND Result;
		} wnd{ *this, nullptr };
		EnumWindows([](HWND hWnd, LPARAM lParam) -> BOOL {
			auto& self = *reinterpret_cast<EnumWindowsStruct*>(lParam);
			DWORD pid;
			GetWindowThreadProcessId(hWnd, &pid);
			if (pid != GetProcessId(self.Process.hProcess))
				return true;

			wchar_t name[256];
			GetClassNameW(hWnd, name, 256);
			if (wcscmp(name, L"FFXIVGAME") != 0)
				return true;

			self.Result = hWnd;

			return false;
		}, reinterpret_cast<LPARAM>(&wnd));

		std::vector<uint8_t> buf;
		buf.reserve(4096);

		std::shared_ptr<void> remotePtr(
			VirtualAllocEx(hProcess, nullptr, buf.capacity(), MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE),
			[](void* p) {
			VirtualFree(p, 0, MEM_RELEASE);
		}
		);

		/*
		0x00: CallWindowProcW
		0x08: SetWindowLongPtrW
		0x10: Previous WndProc
		0x18: pAtkModule
		0x20: pfnRefreshFonts
		
		<offset 0x40>
		# CallWindowProcW(<value>, hWnd, uMsg, wParam, lParam)
		0x00: PUSH RCX
		0x01: PUSH R9
		0x03: MOV R9, R8
		0x06: MOV R8, RDX
		0x09: MOV RDX, RCX
		0x0c: MOV RCX, [RIP - 0x40 - 0x13 + 0x10]
		0x13: CALL [RIP - 0x40 - 0x19 + 0x00]
		# SetWindowLongPtrW(hWnd, GWLP_WNDPROC, <value>)
		0x19: POP RCX
		0x1a: MOV RDX, -4
		0x21: MOV R8, [RIP - 0x40 - 0x28 + 0x10]
		0x28: CALL [RIP - 0x40 - 0x2e + 0x08]
		# AtkModule.vf43(<value>, false, true)
		0x2e: XOR RDX, RDX
		0x31: MOV R8, 1
		0x38: MOV RCX, [RIP - 0x40 - 0x3f + 0x18]
		0x3f: CALL [RIP - 0x40 - 0x45 + 0x20]
		# return
		0x45: RET
		*/
		uint8_t buf1[]{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x51, 0x41, 0x51, 0x4D, 0x89, 0xC1, 0x49, 0x89, 0xD0, 0x48, 0x89, 0xCA, 0x48, 0x8B, 0x0D, 0xBD, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0xA7, 0xFF, 0xFF, 0xFF, 0x59, 0x48, 0xC7, 0xC2, 0xFC, 0xFF, 0xFF, 0xFF, 0x4C, 0x8B, 0x05, 0xA8, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0x9A, 0xFF, 0xFF, 0xFF, 0x48, 0x31, 0xD2, 0x49, 0xC7, 0xC0, 0x01, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0D, 0x99, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0x9B, 0xFF, 0xFF, 0xFF, 0xC3 };
		*reinterpret_cast<void**>(&buf1[0x00]) = GetProcAddress(GetModuleHandleW(L"user32.dll"), "CallWindowProcW");
		*reinterpret_cast<void**>(&buf1[0x08]) = GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowLongPtrW");
		*reinterpret_cast<LONG_PTR*>(&buf1[0x10]) = GetWindowLongPtrW(wnd.Result, GWLP_WNDPROC);
		*reinterpret_cast<void**>(&buf1[0x18]) = pAtkModule;
		*reinterpret_cast<void**>(&buf1[0x20]) = pfnRefreshFonts;

		/*
		0x00: SetWindowLongPtrW
		0x08: ExitThread
		0x10: New WndProc

		<offset 0x40>
		# SetWindowLongPtrW(hWnd=RCX, GWLP_WNDPROC, <value>)
		0x00: MOV RDX, -4
		0x07: MOV R8, [RIP - 0x40 - 0x0e + 0x10]
		0x0e: CALL [RIP - 0x40 - 0x14 + 0x00]
		0x14: MOV [RIP - 0x40 - 0x1b - 134 + 0x10], RAX
		# ExitThread(0)
		0x1b: CALL [RIP - 0x40 - 0x21 + 0x08]
		0x21: RET
		*/
		uint8_t buf2[]{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x48, 0xC7, 0xC2, 0xFC, 0xFF, 0xFF, 0xFF, 0x4C, 0x8B, 0x05, 0xC2, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0xAC, 0xFF, 0xFF, 0xFF, 0x48, 0x89, 0x05, 0x2F, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0xA7, 0xFF, 0xFF, 0xFF, 0xC3 };
		*reinterpret_cast<void**>(&buf2[0x00]) = GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowLongPtrW");
		*reinterpret_cast<void**>(&buf2[0x08]) = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "ExitThread");
		*reinterpret_cast<void**>(&buf2[0x10]) = reinterpret_cast<char*>(remotePtr.get()) + 0x40;

		buf.insert(buf.end(), buf1, buf1 + sizeof buf1);
		buf.insert(buf.end(), buf2, buf2 + sizeof buf2);

		size_t written{};
		WriteProcessMemory(hProcess, remotePtr.get(), &buf[0], buf.size(), &written);

		const auto hRemoteThread = CreateRemoteThread(hProcess, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<char*>(remotePtr.get()) + sizeof buf1 + 0x40), wnd.Result, 0, nullptr);

		__debugbreak();
		// 
	}
};

std::filesystem::path GetModulePath(HANDLE hProcess, HMODULE hModule) {
	std::wstring pathStr(PATHCCH_MAX_CCH + 1, L'\0');
	pathStr.resize(GetModuleFileNameExW(hProcess, hModule, &pathStr[0], static_cast<DWORD>(pathStr.size())));
	return pathStr;
}

GameProcess ReadFontSets(HANDLE hProcess) {
	std::vector<HMODULE> modules;
	DWORD needed = 0;
	do {
		modules.resize(modules.size() + 128);
		EnumProcessModules(hProcess, &modules[0], static_cast<DWORD>(std::span(modules).size_bytes()), &needed);
	} while (std::span(modules).size_bytes() < needed);
	modules.resize(needed / sizeof HMODULE);
	auto path = GetModulePath(hProcess, modules[0]);

	std::shared_ptr<void> hFile(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr), &CloseHandle);
	std::vector<char> buf(GetFileSize(hFile.get(), nullptr));
	DWORD rd;
	if (!ReadFile(hFile.get(), &buf[0], static_cast<DWORD>(buf.size()), &rd, nullptr) || rd != buf.size())
		throw std::runtime_error(std::format("Failed to read file: {}", GetLastError()));

	GameProcess res{
		.hProcess = hProcess,
		.hModule = modules[0],
	};

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
	const auto realBase = reinterpret_cast<char*>(modules[0]);
	const auto imageBase = ntHeader64.OptionalHeader.ImageBase;
	const auto& rva2sec = [&](size_t rva) -> IMAGE_SECTION_HEADER& {
		for (auto& sectionHeader2 : sectionHeaders) {
			if (sectionHeader2.VirtualAddress <= rva && rva < sectionHeader2.VirtualAddress + sectionHeader2.Misc.VirtualSize)
				return sectionHeader2;
		}
		throw std::runtime_error("rva");
	};
	const auto& va2rva = [&](void* va) {
		return reinterpret_cast<uint64_t>(va) - imageBase;
	};
	const auto& va2sec = [&](void* va) -> IMAGE_SECTION_HEADER& {
		return rva2sec(reinterpret_cast<uint64_t>(va) - imageBase);
	};
	for (const auto& sectionHeader : sectionHeaders) {
		const auto section = std::span(&buf[sectionHeader.PointerToRawData], sectionHeader.SizeOfRawData);
		if (strncmp(reinterpret_cast<const char*>(sectionHeader.Name), ".text", IMAGE_SIZEOF_SHORT_NAME) != 0)
			continue;

		if (const auto found = LookupForData(section, Framework_GetUiModulePatternText, Framework_GetUiModulePatternMask); !found.empty()) {
			auto rvaBase = &found[0] - &buf[0] + sectionHeader.VirtualAddress - sectionHeader.PointerToRawData;
			auto rvapFramework = rvaBase + Framework_GetUiModulePattern_FrameworkOffsetOffset + 4 + *reinterpret_cast<int*>(&found[Framework_GetUiModulePattern_FrameworkOffsetOffset]);
			auto rvaGetUiModule = rvaBase + Framework_GetUiModulePattern_GetUiModuleOffsetOffset + 4 + *reinterpret_cast<int*>(&found[Framework_GetUiModulePattern_GetUiModuleOffsetOffset]);

			char* pFramework;
			size_t read{};
			if (!ReadProcessMemory(hProcess, reinterpret_cast<void*>(realBase + rvapFramework), &pFramework, sizeof pFramework, &read))
				throw std::runtime_error("ReadProcessMemory fail");

			if (sectionHeader.VirtualAddress <= rvaGetUiModule && rvaGetUiModule < sectionHeader.VirtualAddress + sectionHeader.Misc.VirtualSize) {
				const auto pfnGetUiModule = &buf[rvaGetUiModule + sectionHeader.PointerToRawData - sectionHeader.VirtualAddress];
				if (memcmp(pfnGetUiModule, "\x48\x85\xc9\x75\x03\x33\xc0\xc3\x48\x8b\x81", 11) == 0) {
					auto frameworkUiModuleOffset = *reinterpret_cast<int*>(&pfnGetUiModule[11]);

					auto ppUiModule = pFramework + frameworkUiModuleOffset;

					void** vtbl{};
					char* pfnGetRaptureAtkModule{};
					char* pUiModule{};
					if (!ReadProcessMemory(hProcess, ppUiModule, &pUiModule, sizeof pUiModule, &read))
						throw std::runtime_error("ReadProcessMemory fail");
					if (!ReadProcessMemory(hProcess, pUiModule, &vtbl, sizeof vtbl, &read))
						throw std::runtime_error("ReadProcessMemory fail");
					if (!ReadProcessMemory(hProcess, vtbl + 7, &pfnGetRaptureAtkModule, sizeof pfnGetRaptureAtkModule, &read))
						throw std::runtime_error("ReadProcessMemory fail");
					pfnGetRaptureAtkModule = &buf[pfnGetRaptureAtkModule - realBase - sectionHeader.VirtualAddress + sectionHeader.PointerToRawData];

					if (memcmp(pfnGetRaptureAtkModule, "\x48\x8d\x81", 3) == 0) {
						auto uiModuleRaptureAtkModuleOffset = *reinterpret_cast<int*>(&pfnGetRaptureAtkModule[3]);
						auto pRaptureAtkModule = pUiModule + uiModuleRaptureAtkModuleOffset;
						res.pAtkModule = pRaptureAtkModule;

						char* pfnRefreshFonts{};
						if (!ReadProcessMemory(hProcess, pRaptureAtkModule, &vtbl, sizeof vtbl, &read))
							throw std::runtime_error("ReadProcessMemory fail");
						if (!ReadProcessMemory(hProcess, vtbl + 43, &pfnRefreshFonts, sizeof pfnRefreshFonts, &read))
							throw std::runtime_error("ReadProcessMemory fail");

						res.pfnRefreshFonts = reinterpret_cast<decltype(res.pfnRefreshFonts)>(pfnRefreshFonts);
					}
				}
			}
		}

		if (const auto found = LookupForData(section, PatternText, PatternMask); !found.empty()) {
			for (std::span<char> range2 = std::span(&found.back() + 1, 128), f; !(f = LookupForData(range2, Pattern2Mask, Pattern2Text)).empty(); range2 = range2.subspan(&f[1] - &range2[0])) {
				auto rva = &f.back() + *reinterpret_cast<int*>(&f[3]) - &buf[0];
				rva += sectionHeader.VirtualAddress - sectionHeader.PointerToRawData;

				const auto& fontSetSection = rva2sec(rva);
				FontSetInGame fontSetSource = *reinterpret_cast<FontSetInGame*>(&buf[rva - fontSetSection.VirtualAddress + fontSetSection.PointerToRawData]);

				auto& fontSet = res.FontSets.emplace_back();
				fontSet.VirtualAddress = rva;

				for (size_t i = 0; i < 0x29; i++) {
					auto& sourceItem = fontSetSource.Faces[i];
					auto& targetItem = fontSet.Faces[i];
					const auto& texPatternSection = va2sec(sourceItem.TexPattern);
					const auto& fdtSection = va2sec(sourceItem.Fdt);
					targetItem.TexPattern = &buf[va2rva(sourceItem.TexPattern) + texPatternSection.PointerToRawData - texPatternSection.VirtualAddress];
					targetItem.Fdt = &buf[va2rva(sourceItem.Fdt) + fdtSection.PointerToRawData - fdtSection.VirtualAddress];
					targetItem.TexCount = sourceItem.TexCount;
				}
			}
		}
	}

	return res;
}

int __stdcall WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int nShowCmd) {
	g_hInstance = hInstance;

	std::vector<DWORD> pids;
	pids.resize(4096);
	DWORD cb;
	EnumProcesses(&pids[0], static_cast<DWORD>(std::span(pids).size_bytes()), &cb);
	pids.resize(cb / sizeof DWORD);
	for (const auto pid : pids) {
		std::wstring path(PATHCCH_MAX_CCH + 1, L'\0');
		const auto hProcess = std::unique_ptr<void, decltype(CloseHandle)*>(OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid), &CloseHandle);
		if (hProcess.get() == nullptr)
			continue;

		path.resize(GetProcessImageFileNameW(hProcess.get(), &path[0], static_cast<DWORD>(path.size())));
		if (path.ends_with(L"ffxiv_dx11.exe"))
			ReadFontSets(hProcess.get()).RefreshFonts();
	}
	// std::vector<std::vector<FontSet>> sets;
	// sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe)"));
	// sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\SNDA\FFXIV\game\ffxiv_dx11.exe)"));
	// sets.emplace_back(ReadFontSets(LR"(C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game\ffxiv_dx11.exe)"));

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
	return 0;

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
