#include "pch.h"
#include "GameFontReloader.h"

template<typename T>
inline T NotNull(T v) {
	if (!v)
		throw std::runtime_error("Fail");
	return v;
}

static const char Framework_GetUiModulePatternText[] = "\x48\x8B\x0D\x00\x00\x00\x00" "\x4C\x89\xA4\x24\x00\x00\x00\x00" "\xE8\x00\x00\x00\x00\x80\x7B\x1D\x01";
static const char Framework_GetUiModulePatternMask[] = "\xFF\xFF\xFF\x00\x00\x00\x00" "\xFF\xFF\xFF\xFF\x00\x00\x00\x00" "\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF";
static const size_t Framework_GetUiModulePattern_FrameworkOffsetOffset = 3;
static const size_t Framework_GetUiModulePattern_GetUiModuleOffsetOffset = 16;

static const char PatternText[] = "\x40\x56\x41\x54\x41\x55\x41\x57\x48\x81\xec\x00\x00\x00\x00\x48\x8b\x05\x00\x00\x00\x00\x48\x33\xc4\x48\x89\x84\x24\x00\x00\x00\x00\x44\x88\x44\x24\x00\x45\x0f\xb6\xf8";
static const char PatternMask[] = "\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF\xFF\xFF\xFF\x00\x00\x00\x00\xFF\xFF\xFF\xFF\x00\xFF\xFF\xFF\xFF";
static const char Pattern2Text[] = "\x48\x8D\x05\x00\x00\x00\x00";
static const char Pattern2Mask[] = "\xFB\xFF\xDF\x00\x00\x00\x00";

static std::span<char> LookupForData(std::span<char> range, std::span<const char> pattern, std::span<const char> mask) {
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

extern "C" void asm_call_atkmodule_vf43_via_wndproc();

static std::vector<std::span<uint8_t>> Segmentize(void* pfn) {
	static constexpr std::array<uint8_t, 8> marker{ { 0x90, 0xcc, 0x90, 0xcc, 0x90, 0xcc, 0x90, 0xcc } };

	auto ptr = reinterpret_cast<uint8_t*>(pfn);
	if (*ptr == 0xE9)
		ptr += 5 + *reinterpret_cast<int*>(ptr + 1);

	std::vector<std::span<uint8_t>> result;

	for (size_t prev = 0, i = 0;; prev = (i += 8)) {
		while (*reinterpret_cast<uint64_t*>(&ptr[i]) != 0xcc90cc90cc90cc90ULL)
			i++;
		if (prev == i)
			break;
		result.emplace_back(&ptr[prev], &ptr[i]);
	}
	return result;
}

static std::filesystem::path GetModulePath(HANDLE hProcess, HMODULE hModule) {
	std::wstring pathStr(PATHCCH_MAX_CCH + 1, L'\0');
	pathStr.resize(GetModuleFileNameExW(hProcess, hModule, &pathStr[0], static_cast<DWORD>(pathStr.size())));
	return pathStr;
}

static HMODULE GetFirstModule(HANDLE hProcess) {
	std::vector<HMODULE> modules;
	DWORD needed = 0;
	do {
		modules.resize(modules.size() + 128);
		EnumProcessModules(hProcess, &modules[0], static_cast<DWORD>(std::span(modules).size_bytes()), &needed);
	} while (std::span(modules).size_bytes() < needed);
	modules.resize(needed / sizeof HMODULE);
	return modules[0];
}

GameFontReloader::GameProcess::GameProcess(DWORD pid)
	: m_hProcess(NotNull(OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid)), &CloseHandle)
	, m_hModule(GetFirstModule(m_hProcess.get()))
	, m_gameExePath(GetModulePath(m_hProcess.get(), m_hModule)) {

	if (m_gameExePath.filename() != L"ffxiv_dx11.exe")
		throw std::runtime_error("Not a ffxiv executable");

	std::shared_ptr<void> hFile(CreateFileW(m_gameExePath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr), &CloseHandle);
	std::vector<char> buf(GetFileSize(hFile.get(), nullptr));
	DWORD rd;
	if (!ReadFile(hFile.get(), &buf[0], static_cast<DWORD>(buf.size()), &rd, nullptr) || rd != buf.size())
		throw std::runtime_error(std::format("Failed to read file: {}", GetLastError()));

	const auto& dosHeader = *reinterpret_cast<IMAGE_DOS_HEADER*>(&buf[0]);
	const auto& ntHeader64 = *reinterpret_cast<IMAGE_NT_HEADERS64*>(&buf[dosHeader.e_lfanew]);
	const auto sectionHeaders = std::span(IMAGE_FIRST_SECTION(&ntHeader64), ntHeader64.FileHeader.NumberOfSections);
	const auto realBase = reinterpret_cast<char*>(m_hModule);
	const auto imageBase = reinterpret_cast<char*>(ntHeader64.OptionalHeader.ImageBase);
	const auto& rva2sec = [&](size_t rva) -> IMAGE_SECTION_HEADER& {
		for (auto& sectionHeader2 : sectionHeaders) {
			if (sectionHeader2.VirtualAddress <= rva && rva < sectionHeader2.VirtualAddress + sectionHeader2.Misc.VirtualSize)
				return sectionHeader2;
		}
		throw std::runtime_error("rva");
	};
	const auto& va2rva = [&](void* va) {
		return reinterpret_cast<char*>(va) - imageBase;
	};
	const auto& va2sec = [&](void* va) -> IMAGE_SECTION_HEADER& {
		return rva2sec(reinterpret_cast<char*>(va) - imageBase);
	};
	for (const auto& sectionHeader : sectionHeaders) {
		const auto section = std::span(&buf[sectionHeader.PointerToRawData], sectionHeader.SizeOfRawData);
		if (strncmp(reinterpret_cast<const char*>(sectionHeader.Name), ".text", IMAGE_SIZEOF_SHORT_NAME) != 0)
			continue;

		if (const auto found = LookupForData(section, Framework_GetUiModulePatternText, Framework_GetUiModulePatternMask); !found.empty()) {
			auto rvaBase = &found[0] - &buf[0] + sectionHeader.VirtualAddress - sectionHeader.PointerToRawData;
			auto rvapFramework = rvaBase + Framework_GetUiModulePattern_FrameworkOffsetOffset + 4 + *reinterpret_cast<int*>(&found[Framework_GetUiModulePattern_FrameworkOffsetOffset]);
			auto rvaGetUiModule = rvaBase + Framework_GetUiModulePattern_GetUiModuleOffsetOffset + 4 + *reinterpret_cast<int*>(&found[Framework_GetUiModulePattern_GetUiModuleOffsetOffset]);

			m_ppFramework = realBase + rvapFramework;
			m_pfnFrameworkGetUiModule = realBase + rvaGetUiModule;
		}

		if (const auto found = LookupForData(section, PatternText, PatternMask); !found.empty()) {
			for (std::span<char> range2 = std::span(&found.back() + 1, 128), f; !(f = LookupForData(range2, Pattern2Mask, Pattern2Text)).empty(); range2 = range2.subspan(&f[1] - &range2[0])) {
				auto rva = &f.back() + *reinterpret_cast<int*>(&f[3]) - &buf[0];
				rva = rva + sectionHeader.VirtualAddress - sectionHeader.PointerToRawData;

				const auto& fontSetSection = rva2sec(rva);
				auto& fontSet = m_sets.emplace_back();
				auto& originals = m_originals.emplace_back() = *reinterpret_cast<FontSetInGame*>(&buf[rva - fontSetSection.VirtualAddress + fontSetSection.PointerToRawData]);
				m_setAddresses.emplace_back(realBase + rva);

				for (size_t i = 0; i < 0x29; i++) {
					auto& sourceItem = originals.Faces[i];
					auto& targetItem = fontSet.Faces[i];
					const auto& texPatternSection = va2sec(sourceItem.TexPattern);
					const auto& fdtSection = va2sec(sourceItem.Fdt);
					targetItem.TexPattern = &buf[va2rva(sourceItem.TexPattern) + texPatternSection.PointerToRawData - texPatternSection.VirtualAddress];
					targetItem.Fdt = &buf[va2rva(sourceItem.Fdt) + fdtSection.PointerToRawData - fdtSection.VirtualAddress];
					targetItem.TexCount = sourceItem.TexCount;
					sourceItem.TexPattern = sourceItem.TexPattern + (realBase - imageBase);
					sourceItem.Fdt = sourceItem.Fdt + (realBase - imageBase);
				}
			}
		}
	}
}

void GameFontReloader::GameProcess::RefreshFonts(const FontSet* pTargetSet) const {
	HWND hGameWindow = nullptr;
	{
		struct EnumWindowsStruct {
			const GameProcess& Process;
			HWND Result;
		} wnd{ *this, nullptr };
		EnumWindows([](HWND hWnd, LPARAM lParam) -> BOOL {
			auto& self = *reinterpret_cast<EnumWindowsStruct*>(lParam);
			DWORD pid;
			GetWindowThreadProcessId(hWnd, &pid);
			if (pid != GetProcessId(self.Process.m_hProcess.get()))
				return true;

			wchar_t name[256];
			GetClassNameW(hWnd, name, 256);
			if (wcscmp(name, L"FFXIVGAME") != 0)
				return true;

			self.Result = hWnd;

			return false;
		}, reinterpret_cast<LPARAM>(&wnd));
		if (wnd.Result)
			hGameWindow = wnd.Result;
		else
			throw std::runtime_error("Game window not found.");
	}

	auto segments = Segmentize(&asm_call_atkmodule_vf43_via_wndproc);
	std::vector<uint8_t> code(1 + &segments.back().back() - &segments.front().front());
	memcpy(&code[0], &segments.front().front(), code.size());

	FontSetInGame target{};
	if (pTargetSet) {
		for (size_t i = 0; i < FaceCount; i++) {
			const auto& local = pTargetSet->Faces[i];
			auto& remote = target.Faces[i];
			remote.TexCount = local.TexCount;

			remote.TexPattern = reinterpret_cast<char*>(code.size());
			code.insert(code.end(),
				reinterpret_cast<const uint8_t*>(local.TexPattern.data()),
				reinterpret_cast<const uint8_t*>(local.TexPattern.data()) + local.TexPattern.size());
			code.push_back(0);

			remote.Fdt = reinterpret_cast<char*>(code.size());
			code.insert(code.end(),
				reinterpret_cast<const uint8_t*>(local.Fdt.data()),
				reinterpret_cast<const uint8_t*>(local.Fdt.data()) + local.Fdt.size());
			code.push_back(0);
		}
	}

	const auto offsetMyWndProc = &segments[1].front() - &segments[0].front();
	const auto offsetRedirectWndProc = &segments[2].front() - &segments[0].front();
	const std::unique_ptr<void, decltype(CloseHandle)*> hEvent1(NotNull(CreateEventW(nullptr, TRUE, FALSE, nullptr)), &CloseHandle);
	const std::unique_ptr<void, decltype(CloseHandle)*> hEvent2(NotNull(CreateEventW(nullptr, TRUE, FALSE, nullptr)), &CloseHandle);
	const auto pRemote = reinterpret_cast<char*>(NotNull(VirtualAllocEx(m_hProcess.get(), nullptr, code.capacity(), MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE)));
	std::shared_ptr<void> remotePtrFreer;

	HANDLE hEvent1Target;
	if (!DuplicateHandle(GetCurrentProcess(), hEvent1.get(), m_hProcess.get(), &hEvent1Target, 0, FALSE, DUPLICATE_SAME_ACCESS))
		throw std::runtime_error("DuplicateHandle fail");

	HANDLE hEvent2Target;
	if (!DuplicateHandle(GetCurrentProcess(), hEvent2.get(), m_hProcess.get(), &hEvent2Target, 0, FALSE, DUPLICATE_SAME_ACCESS))
		throw std::runtime_error("DuplicateHandle fail");

	const auto pData = reinterpret_cast<void**>(&code[0]);
	pData[0] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "CallWindowProcW"));
	pData[1] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "SetWindowLongPtrW"));
	pData[2] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "GetWindowLongPtrW"));
	pData[3] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "SetEvent"));
	pData[4] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "ResetEvent"));
	pData[5] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "WaitForSingleObject"));
	pData[6] = m_ppFramework;
	pData[7] = m_pfnFrameworkGetUiModule;
	pData[8] = hGameWindow;
	pData[9] = hEvent1Target;
	pData[10] = hEvent2Target;

	if (pTargetSet) {
		for (auto& remote : target.Faces) {
			remote.Fdt = pRemote + reinterpret_cast<size_t>(remote.Fdt);
			remote.TexPattern = pRemote + reinterpret_cast<size_t>(remote.TexPattern);
		}
	} else {
		// Unless we're restoring the string data, we cannot free the newly allocated memory
		remotePtrFreer = { pRemote,[hProcess = m_hProcess.get()](void* p) { VirtualFreeEx(hProcess, p, 0, MEM_RELEASE); } };
	}

	size_t written{};
	WriteProcessMemory(m_hProcess.get(), pRemote, &code[0], code.size(), &written);

	const auto hRemoteThread = NotNull(CreateRemoteThread(m_hProcess.get(), nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(pRemote + offsetRedirectWndProc), nullptr, 0, nullptr));
	WaitForSingleObject(hRemoteThread, INFINITE);
	PostMessage(hGameWindow, WM_NULL, 0, 0);

	WaitForSingleObject(hEvent1.get(), INFINITE);
	if (pTargetSet) {
		for (size_t i = 0; i < m_setAddresses.size(); i++) {
			size_t written{};
			WriteProcessMemory(m_hProcess.get(), m_setAddresses[i], &target, sizeof target, &written);
		}
	} else {
		for (size_t i = 0; i < m_setAddresses.size(); i++) {
			size_t written{};
			WriteProcessMemory(m_hProcess.get(), m_setAddresses[i], &m_originals[i], sizeof m_originals[i], &written);
		}
	}
	ResetEvent(hEvent1.get());
	SetEvent(hEvent2.get());

	SendMessage(hGameWindow, WM_NULL, 0, 0);

	DuplicateHandle(m_hProcess.get(), hEvent1Target, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
	DuplicateHandle(m_hProcess.get(), hEvent2Target, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
}

const GameFontReloader::FontSet& GameFontReloader::GetDefaultFontSet(XivRes::GameFontType type) {
	static const FontSet PresetFont{ {
		{ 7, "font%d.tex", "AXIS_18.fdt" },
		{ 7, "font%d.tex", "AXIS_14.fdt" },
		{ 7, "font%d.tex", "AXIS_12.fdt" },
		{ 7, "font%d.tex", "AXIS_96.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_36.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_18.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_14.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_12.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_10.fdt" },
		{ 7, "font%d.tex", "Meidinger_40.fdt" },
		{ 7, "font%d.tex", "Meidinger_20.fdt" },
		{ 7, "font%d.tex", "Meidinger_16.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_68.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_34.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_23.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_184.fdt" },
		{ 7, "font%d.tex", "Jupiter_46.fdt" },
		{ 7, "font%d.tex", "Jupiter_23.fdt" },
		{ 7, "font%d.tex", "Jupiter_20.fdt" },
		{ 7, "font%d.tex", "Jupiter_16.fdt" },
		{ 7, "font%d.tex", "Jupiter_90.fdt" },
		{ 7, "font%d.tex", "Jupiter_45.fdt" },
		{ 7, "font%d.tex", "AXIS_36.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_36.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_18.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_14.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_12.fdt" },
		{ 7, "font%d.tex", "MiedingerMid_10.fdt" },
		{ 7, "font%d.tex", "Meidinger_40.fdt" },
		{ 7, "font%d.tex", "Meidinger_20.fdt" },
		{ 7, "font%d.tex", "Meidinger_16.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_68.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_34.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_23.fdt" },
		{ 7, "font%d.tex", "TrumpGothic_184.fdt" },
		{ 7, "font%d.tex", "Jupiter_46.fdt" },
		{ 7, "font%d.tex", "Jupiter_23.fdt" },
		{ 7, "font%d.tex", "Jupiter_20.fdt" },
		{ 7, "font%d.tex", "Jupiter_16.fdt" },
		{ 7, "font%d.tex", "Jupiter_90.fdt" },
		{ 7, "font%d.tex", "Jupiter_45.fdt" },
	} };

	static const FontSet PresetFontLobby{ {
		{ 6, "font_lobby%d.tex", "AXIS_18_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "AXIS_14_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "AXIS_12_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "AXIS_12_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_36_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_18_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_14_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_12_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_10_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_40_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_20_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_16_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_68_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_34_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_23_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_184_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_46_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_23_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_20_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_16_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_90_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_45_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "AXIS_36_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_36_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_18_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_14_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_12_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "MiedingerMid_10_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_40_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_20_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Meidinger_16_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_68_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_34_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_23_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "TrumpGothic_184_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_46_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_23_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_20_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_16_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_90_lobby.fdt" },
		{ 6, "font_lobby%d.tex", "Jupiter_45_lobby.fdt" },
	} };

	static const FontSet PresetFontChnAxis{ {
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_140.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_120.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_120.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_180.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 20, "font_chn_%d.tex", "ChnAXIS_360.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_36.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_18.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_14.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_12.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_10.fdt" },
		{ 3, "font%d.tex", "Meidinger_40.fdt" },
		{ 3, "font%d.tex", "Meidinger_20.fdt" },
		{ 3, "font%d.tex", "Meidinger_16.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_68.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_34.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_23.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_184.fdt" },
		{ 3, "font%d.tex", "Jupiter_46.fdt" },
		{ 3, "font%d.tex", "Jupiter_23.fdt" },
		{ 3, "font%d.tex", "Jupiter_20.fdt" },
		{ 3, "font%d.tex", "Jupiter_16.fdt" },
		{ 3, "font%d.tex", "Jupiter_90.fdt" },
		{ 3, "font%d.tex", "Jupiter_45.fdt" },
	} };

	static const FontSet PresetFontKrnAxis{ {
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_140.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_120.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_120.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_180.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 9, "font_krn_%d.tex", "KrnAXIS_360.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_36.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_18.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_14.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_12.fdt" },
		{ 3, "font%d.tex", "MiedingerMid_10.fdt" },
		{ 3, "font%d.tex", "Meidinger_40.fdt" },
		{ 3, "font%d.tex", "Meidinger_20.fdt" },
		{ 3, "font%d.tex", "Meidinger_16.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_68.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_34.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_23.fdt" },
		{ 3, "font%d.tex", "TrumpGothic_184.fdt" },
		{ 3, "font%d.tex", "Jupiter_46.fdt" },
		{ 3, "font%d.tex", "Jupiter_23.fdt" },
		{ 3, "font%d.tex", "Jupiter_20.fdt" },
		{ 3, "font%d.tex", "Jupiter_16.fdt" },
		{ 3, "font%d.tex", "Jupiter_90.fdt" },
		{ 3, "font%d.tex", "Jupiter_45.fdt" },
	} };

	switch (type) {
		case XivRes::GameFontType::font: return PresetFont;
		case XivRes::GameFontType::font_lobby: return PresetFontLobby;
		case XivRes::GameFontType::chn_axis: return PresetFontChnAxis; return PresetFontChnAxis;
		case XivRes::GameFontType::krn_axis: return PresetFontKrnAxis;
		default: throw std::out_of_range("font/lobby/chn/krn are supported");
	}
}
