#include "pch.h"
#include "resource.h"

#include "CommandLineRender.h"
#include "ExportPreviewWindow.h"
#include "FaceElementEditorDialog.h"
#include "FontChanger.Presets/Structs.h"
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
	if (int nArgs{}; LPWSTR* szArgList = CommandLineToArgvW(GetCommandLineW(), &nArgs)) {
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

	if (const auto r = TryCatchShowError(nullptr, IDS_ERROR_OPENFILEFAILURE_BODY, 1, [&] {
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
		return 0;
	}))
		return r;

	App::Structs::SetGameInstallationPathsProvider([](xivres::font_type fontType) {
		std::vector<std::filesystem::path> paths;
		switch (fontType) {
			case xivres::font_type::chn_axis:
				paths = g_config.China;
				break;
			case xivres::font_type::krn_axis:
				paths = g_config.Korea;
				break;
			case xivres::font_type::tc_axis:
				paths = g_config.TraditionalChinese;
				break;
			default:
				// Every release has the fonts of the global one.
				for (const auto& list : {g_config.Global, g_config.China, g_config.Korea, g_config.TraditionalChinese})
					paths.insert(paths.end(), list.begin(), list.end());
				break;
		}
		return paths;
	});
	App::Structs::SetGameFontErrorHandler([](const std::exception& e) {
		if (const auto systemError = dynamic_cast<const std::system_error*>(&e))
			ShowErrorMessageBox(nullptr, IDS_ERROR_GAMENOTFOUND_BODY, *systemError);
		else
			ShowErrorMessageBox(nullptr, IDS_ERROR_GAMENOTFOUND_BODY, e);
	});

	if (const auto exitCode = App::RunCommandLineRender(args))
		return *exitCode;

	App::FontEditorWindow window(std::move(args));
	for (MSG msg{}; GetMessageW(&msg, nullptr, 0, 0);) {
		if (App::BaseWindow::ConsumeMessage(msg))
			continue;

		TranslateMessage(&msg);
		DispatchMessageW(&msg);
	}

	return 0;
}
