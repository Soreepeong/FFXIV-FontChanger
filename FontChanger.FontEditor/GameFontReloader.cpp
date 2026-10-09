#include "pch.h"
#include "GameFontReloader.h"
#include "resource.h"

#include "FontChanger.Presets/Structs.h"
#include "xivres/game_layout.h"

template<typename T>
inline T NotNull(T v) {
	if (!v)
		throw std::runtime_error("Fail");
	return v;
}

extern "C" void asm_call_atkmodule_vf43_via_wndproc();

// The signatures of the game's code (xivres's data/game_font_signatures.json), compiled in as a resource.
static std::string_view GetGameFontSignatures() {
	const auto hRes = NotNull(FindResourceW(nullptr, MAKEINTRESOURCEW(IDR_GAMEFONTSIGNATURES), RT_RCDATA));
	const auto hGlob = NotNull(LoadResource(nullptr, hRes));
	return {static_cast<const char*>(LockResource(hGlob)), SizeofResource(nullptr, hRes)};
}

static std::vector<std::span<uint8_t>> Segmentize(void* pfn) {
	static constexpr std::array<uint8_t, 8> marker{{0x90, 0xcc, 0x90, 0xcc, 0x90, 0xcc, 0x90, 0xcc}};

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
	pathStr.resize(GetModuleFileNameExW(hProcess, hModule, pathStr.data(), static_cast<DWORD>(pathStr.size())));
	return pathStr;
}

static HMODULE GetFirstModule(HANDLE hProcess) {
	std::vector<HMODULE> modules;
	DWORD needed = 0;
	do {
		modules.resize(modules.size() + 128);
		EnumProcessModules(hProcess, modules.data(), static_cast<DWORD>(std::span(modules).size_bytes()), &needed);
	} while (std::span(modules).size_bytes() < needed);
	modules.resize(needed / sizeof HMODULE);
	if (modules.empty())
		throw std::runtime_error("Process not accessible");
	return modules[0];
}

GameFontReloader::GameProcess::GameProcess(DWORD pid)
	: m_hProcess(NotNull(OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid)), &CloseHandle)
	, m_hModule(GetFirstModule(m_hProcess.get()))
	, m_gameExePath(GetModulePath(m_hProcess.get(), m_hModule)) {
	if (m_gameExePath.filename() != L"ffxiv_dx11.exe")
		throw std::runtime_error("Not a ffxiv executable");

	// The code is read from the executable rather than from the game, so hooks that others placed don't hide it.
	std::vector<uint8_t> exe(std::filesystem::file_size(m_gameExePath));
	if (std::ifstream file(m_gameExePath, std::ios::binary); !file.read(reinterpret_cast<char*>(exe.data()), static_cast<std::streamsize>(exe.size())))
		throw std::runtime_error("Failed to read the game's executable.");

	const auto realBase = reinterpret_cast<uint64_t>(m_hModule);
	xivres::game_layout layout(GetGameFontSignatures(), exe, realBase);
	std::vector<uint64_t> tableAddresses;
	int32_t tableEntrySize{}, tableCount{};
	layout.resolve("Hot reload", [&] {
		m_game.ppFramework = reinterpret_cast<void*>(layout.target("Framework.Instance"));
		m_game.pfnFrameworkGetUiModule = reinterpret_cast<void*>(layout.target("Framework.GetUIModule"));
		m_game.UIModule_GetRaptureAtkModule_VtableOffset = layout.get("UIModule.GetRaptureAtkModule.VtableOffset");
		m_game.pfnAtkModuleLoadFonts = reinterpret_cast<void*>(layout.address("AtkModuleLoadFonts"));
		m_game.AtkModule_IsLobby = layout.get("AtkModule.IsLobby");
		m_game.ppAtkStage = reinterpret_cast<void*>(layout.target("AtkStage.Instance"));
		m_game.AtkStage_RaptureAtkUnitManager = layout.get("AtkStage.RaptureAtkUnitManager");
		m_game.pfnAtkUnitManagerGetAddonByName = reinterpret_cast<void*>(layout.target("AtkUnitManager.GetAddonByName"));
		m_game.AddonNamePlate_NamePlateObjects = layout.get("AddonNamePlate.NamePlateObjectArray");
		m_game.NamePlateObject_Size = layout.get("NamePlateObject");
		m_game.NamePlateObject_NameText = layout.get("NamePlateObject.NameText");
		m_game.NamePlateObjectCount = layout.get("NamePlateObjectArray") / std::max(1, layout.get("NamePlateObject"));
		m_game.pfnAtkTextNodeToggleFontCache = reinterpret_cast<void*>(layout.address("ToggleFontCache"));
		m_game.AtkTextNode_FontCacheFlags = layout.get("AtkTextNode.FontCacheFlags");
		m_game.UseFontCacheFlag = layout.get("AtkTextNode.FontCacheFlags.UseFontCache");
		tableAddresses = {layout.target("GameTable"), layout.target("LobbyTableA"), layout.target("LobbyTableB")};
		tableEntrySize = layout.get("FontTableEntry");
		tableCount = layout.get("FontTable.Count");
	});
	if (tableEntrySize != sizeof(FontSetInGame::Face) || tableCount != FaceCount)
		throw std::runtime_error("The game's font tables changed.");

	// The tables as the executable has them; their pointers are of the executable's preferred base.
	const auto& dosHeader = *reinterpret_cast<const IMAGE_DOS_HEADER*>(exe.data());
	const auto& ntHeader64 = *reinterpret_cast<const IMAGE_NT_HEADERS64*>(&exe[dosHeader.e_lfanew]);
	const auto sectionHeaders = std::span(IMAGE_FIRST_SECTION(&ntHeader64), ntHeader64.FileHeader.NumberOfSections);
	const auto imageBase = ntHeader64.OptionalHeader.ImageBase;
	const auto fileOffset = [&](uint64_t rva) -> size_t {
		for (const auto& s : sectionHeaders) {
			if (s.VirtualAddress <= rva && rva < s.VirtualAddress + s.Misc.VirtualSize)
				return rva - s.VirtualAddress + s.PointerToRawData;
		}
		throw std::runtime_error("rva");
	};
	for (const auto address : tableAddresses) {
		if (std::ranges::any_of(m_tables, [&](const auto& t) { return t.Address == reinterpret_cast<void*>(address); }))
			continue;

		auto& table = m_tables.emplace_back(reinterpret_cast<void*>(address));
		memcpy(&table.Original, &exe[fileOffset(address - realBase)], sizeof table.Original);
		for (size_t i = 0; i < FaceCount; i++) {
			auto& source = table.Original.Faces[i];
			auto& target = table.Faces.Faces[i];
			target.TexCount = source.TexCount;
			target.TexPattern = reinterpret_cast<const char*>(&exe[fileOffset(reinterpret_cast<uint64_t>(source.TexPattern) - imageBase)]);
			target.Fdt = reinterpret_cast<const char*>(&exe[fileOffset(reinterpret_cast<uint64_t>(source.Fdt) - imageBase)]);
			source.TexPattern += realBase - imageBase;
			source.Fdt += realBase - imageBase;
		}
	}

	for (const auto type : {xivres::font_type::font, xivres::font_type::chn_axis, xivres::font_type::krn_axis}) {
		const auto defaults = FontChanger::FixedSizeFont::get_font_table(type);
		if (defaults.size() == FaceCount && std::ranges::equal(m_tables.front().Faces.Faces, defaults, [](const auto& f, const auto& d) { return f.Fdt == d.Face->Name + ".fdt"; }))
			m_fontType = type;
	}
}

void GameFontReloader::GameProcess::RefreshFonts(const FontChanger::Structs::MultiFontSet* pProject, xivres::font_type fontType) const {
	if (pProject && fontType != xivres::font_type::undefined && fontType != xivres::font_type::font && fontType != m_fontType)
		throw std::runtime_error("The game has no fonts of the font type.");

	// What each table will be: as the executable has it without a project; with the faces of the project loaded from the
	// project's textures; or the game's table of a font type, the lobby's staying.
	std::vector<std::optional<FontSet>> targets(m_tables.size());
	if (pProject) {
		for (size_t i = 0; i < m_tables.size(); i++) {
			if (fontType != xivres::font_type::undefined) {
				if (i == 0)
					targets[i] = GetDefaultFontSet(fontType);
				continue;
			}

			auto& target = targets[i].emplace(m_tables[i].Faces);
			for (const auto& f : pProject->FontSets) {
				std::string texNameFormat = f->TexFilenameFormat;
				for (size_t pos; (pos = texNameFormat.find("{}")) != std::string::npos;)
					texNameFormat.replace(pos, 2, "%d");

				for (const auto& face : f->Faces) {
					for (auto& slot : target.Faces) {
						if (slot.Fdt == face->Name + ".fdt" && slot.TexPattern != texNameFormat) {
							slot.TexPattern = texNameFormat;
							slot.TexCount = f->ExpectedTexCount;
						}
					}
				}
			}
		}
	}

	HWND hGameWindow = nullptr;
	{
		struct EnumWindowsStruct {
			const GameProcess& Process;
			HWND Result;
		} wnd{*this, nullptr};
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
	memcpy(code.data(), &segments.front().front(), code.size());

	// The tables to write, the original if unchanged; the strings of a changed one go after the code, at offsets from
	// it until the code is placed.
	std::vector<FontSetInGame> written(m_tables.size());
	for (size_t t = 0; t < m_tables.size(); t++) {
		written[t] = m_tables[t].Original;
		if (!targets[t])
			continue;
		for (size_t i = 0; i < FaceCount; i++) {
			const auto& local = targets[t]->Faces[i];
			auto& remote = written[t].Faces[i];
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
	const std::unique_ptr<void, decltype(&CloseHandle)> hEvent1(NotNull(CreateEventW(nullptr, TRUE, FALSE, nullptr)), &CloseHandle);
	const std::unique_ptr<void, decltype(&CloseHandle)> hEvent2(NotNull(CreateEventW(nullptr, TRUE, FALSE, nullptr)), &CloseHandle);
	const auto pRemote = reinterpret_cast<char*>(NotNull(VirtualAllocEx(m_hProcess.get(), nullptr, code.capacity(), MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE)));
	std::shared_ptr<void> remotePtrFreer;

	HANDLE hEvent1Target;
	if (!DuplicateHandle(GetCurrentProcess(), hEvent1.get(), m_hProcess.get(), &hEvent1Target, 0, FALSE, DUPLICATE_SAME_ACCESS))
		throw std::runtime_error("DuplicateHandle fail");

	HANDLE hEvent2Target;
	if (!DuplicateHandle(GetCurrentProcess(), hEvent2.get(), m_hProcess.get(), &hEvent2Target, 0, FALSE, DUPLICATE_SAME_ACCESS))
		throw std::runtime_error("DuplicateHandle fail");

	const auto pData = reinterpret_cast<void**>(code.data());
	pData[0] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "CallWindowProcW"));
	pData[1] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "SetWindowLongPtrW"));
	pData[2] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"user32.dll")), "GetWindowLongPtrW"));
	pData[3] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "SetEvent"));
	pData[4] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "ResetEvent"));
	pData[5] = NotNull(GetProcAddress(NotNull(GetModuleHandleW(L"kernel32.dll")), "WaitForSingleObject"));
	pData[6] = m_game.ppFramework;
	pData[7] = m_game.pfnFrameworkGetUiModule;
	pData[8] = hGameWindow;
	pData[9] = hEvent1Target;
	pData[10] = hEvent2Target;
	// pData[11] is the previous window procedure, set by the injected code.
	pData[12] = m_game.pfnAtkUnitManagerGetAddonByName;
	pData[13] = m_game.pfnAtkTextNodeToggleFontCache;
	pData[14] = m_game.ppAtkStage;
	pData[15] = reinterpret_cast<void*>(m_game.AtkModule_IsLobby);
	pData[16] = reinterpret_cast<void*>(m_game.AtkStage_RaptureAtkUnitManager);
	pData[17] = reinterpret_cast<void*>(m_game.AddonNamePlate_NamePlateObjects);
	pData[18] = reinterpret_cast<void*>(m_game.NamePlateObject_Size);
	pData[19] = reinterpret_cast<void*>(m_game.NamePlateObject_NameText);
	pData[20] = reinterpret_cast<void*>(m_game.AtkTextNode_FontCacheFlags);
	pData[21] = reinterpret_cast<void*>(m_game.NamePlateObjectCount);
	pData[22] = reinterpret_cast<void*>(m_game.UIModule_GetRaptureAtkModule_VtableOffset);
	pData[23] = m_game.pfnAtkModuleLoadFonts;
	pData[24] = reinterpret_cast<void*>(m_game.UseFontCacheFlag);

	for (size_t t = 0; t < m_tables.size(); t++) {
		if (!targets[t])
			continue;
		for (auto& remote : written[t].Faces) {
			remote.Fdt = pRemote + reinterpret_cast<size_t>(remote.Fdt);
			remote.TexPattern = pRemote + reinterpret_cast<size_t>(remote.TexPattern);
		}
	}

	// The memory can be freed afterwards unless a table points to strings in it.
	if (std::ranges::none_of(targets, [](const auto& t) { return t.has_value(); }))
		remotePtrFreer = {pRemote, [hProcess = m_hProcess.get()](void* p) { VirtualFreeEx(hProcess, p, 0, MEM_RELEASE); }};

	WriteProcessMemory(m_hProcess.get(), pRemote, code.data(), code.size(), nullptr);

	const auto hRemoteThread = NotNull(CreateRemoteThread(m_hProcess.get(), nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(pRemote + offsetRedirectWndProc), nullptr, 0, nullptr));
	WaitForSingleObject(hRemoteThread, INFINITE);
	PostMessage(hGameWindow, WM_NULL, 0, 0);

	WaitForSingleObject(hEvent1.get(), INFINITE);
	for (size_t t = 0; t < m_tables.size(); t++)
		WriteProcessMemory(m_hProcess.get(), m_tables[t].Address, &written[t], sizeof written[t], nullptr);
	ResetEvent(hEvent1.get());
	SetEvent(hEvent2.get());

	SendMessage(hGameWindow, WM_NULL, 0, 0);

	DuplicateHandle(m_hProcess.get(), hEvent1Target, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
	DuplicateHandle(m_hProcess.get(), hEvent2Target, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
}

GameFontReloader::FontSet GameFontReloader::GetDefaultFontSet(xivres::font_type type) {
	const auto table = FontChanger::FixedSizeFont::get_font_table(type);
	if (table.size() != FaceCount)
		throw std::out_of_range("font/lobby/chn/krn are supported");

	FontSet res;
	for (size_t i = 0; i < FaceCount; i++) {
		std::string_view format(FontChanger::FixedSizeFont::get_font_tex_filename_format(table[i].Face->FontType));
		auto texPattern = std::string(format.substr(format.rfind('/') + 1));
		texPattern.replace(texPattern.find("{}"), 2, "%d");
		res.Faces[i] = {table[i].TextureCount, std::move(texPattern), table[i].Face->Name + ".fdt"};
	}
	return res;
}
