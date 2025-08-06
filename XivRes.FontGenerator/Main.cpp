#include "pch.h"
#include "resource.h"

#include "ExportPreviewWindow.h"
#include "FaceElementEditorDialog.h"
#include "Structs.h"
#include "MainWindow.h"
#include "FontGeneratorConfig.h"

#pragma comment(lib, "Comctl32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

HINSTANCE g_hInstance;
WORD g_langId;
std::wstring g_localeName;
FontGeneratorConfig g_config;

int __stdcall WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int nShowCmd) {
	g_hInstance = hInstance;

	SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
	if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)))
		return -1;

	std::vector<std::wstring> args;
	if (int nArgs{}; LPWSTR * szArgList = CommandLineToArgvW(GetCommandLineW(), &nArgs)) {
		if (szArgList) {
			for (int i = 0; i < nArgs; i++)
				args.emplace_back(szArgList[i]);
			LocalFree(szArgList);
		}
	}

	if (wchar_t localeName[LOCALE_NAME_MAX_LENGTH];
		GetUserDefaultLocaleName(localeName, LOCALE_NAME_MAX_LENGTH)) {
		g_localeName = localeName;
	} else {
		g_localeName = L"en-us";
	}

	g_langId = LANGIDFROMLCID(LocaleNameToLCID(g_localeName.c_str(), LOCALE_ALLOW_NEUTRAL_NAMES));

	try {
		if (!exists(FontGeneratorConfig::GetConfigPath())) {
			nlohmann::json json;
			to_json(json, g_config = FontGeneratorConfig::Default);

			std::ofstream configFile(FontGeneratorConfig::GetConfigPath());
			configFile << json;
		} else if (std::ifstream configFile(FontGeneratorConfig::GetConfigPath()); configFile) {
			nlohmann::json json;
			configFile >> json;
			from_json(json, g_config);
			if (!g_config.Language.empty()) {
				g_localeName = xivres::util::unicode::convert<std::wstring>(g_config.Language);
				g_langId = LANGIDFROMLCID(LocaleNameToLCID(g_localeName.c_str(), LOCALE_ALLOW_NEUTRAL_NAMES));
			}
		}
	} catch (const std::exception& e) {
		MessageBoxW(
			nullptr,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_CONFIGFILEERROR, g_langId),
				xivres::util::unicode::convert<std::wstring>(e.what())).c_str(),
			std::wstring(GetStringResource(IDS_APP, g_langId)).c_str(),
			MB_OK | MB_ICONWARNING);
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

HRESULT SuccessOrThrow(HRESULT hr, std::initializer_list<HRESULT> acceptables) {
	if (SUCCEEDED(hr))
		return hr;

	for (const auto& h : acceptables) {
		if (h == hr)
			return hr;
	}

	const auto err = _com_error(hr);
	wchar_t* pszMsg = nullptr;
	FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER |
		FORMAT_MESSAGE_FROM_SYSTEM |
		FORMAT_MESSAGE_IGNORE_INSERTS,
		nullptr,
		hr,
		MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US),
		reinterpret_cast<LPWSTR>(&pszMsg),
		0,
		NULL);
	if (pszMsg) {
		std::unique_ptr<wchar_t, decltype(&LocalFree)> pszMsgFree(pszMsg, &LocalFree);

		throw std::runtime_error(std::format(
			"Error (HRESULT=0x{:08X}): {}",
			static_cast<uint32_t>(hr),
			xivres::util::unicode::convert<std::string>(std::wstring(pszMsg))
		));
	} else {
		throw std::runtime_error(std::format(
			"Error (HRESULT=0x{:08X})",
			static_cast<uint32_t>(hr),
			xivres::util::unicode::convert<std::string>(std::wstring(pszMsg))
		));
	}
}

std::wstring_view GetStringResource(UINT uId, UINT langId) {
	// Convert the string ID into a bundle number
	LPCWSTR pwsz = NULL;
	const auto hrsrc = FindResourceExW(g_hInstance, RT_STRING, MAKEINTRESOURCEW(uId / 16 + 1), langId);
	if (hrsrc) {
		const auto hglob = LoadResource(g_hInstance, hrsrc);
		if (hglob) {
			pwsz = reinterpret_cast<LPCWSTR>(LockResource(hglob));
			if (pwsz) {
				// okay now walk the string table
				for (int i = uId & 15; i > 0; --i)
					pwsz += 1 + static_cast<UINT>(*pwsz);
				UnlockResource(pwsz);
			}
			FreeResource(hglob);
		}
	}
	return pwsz ? std::wstring_view{pwsz + 1, static_cast<size_t>(*pwsz)} : std::wstring_view{};
}

std::wstring_view GetStringResource(UINT id) {
	return GetStringResource(id, g_langId);
}
