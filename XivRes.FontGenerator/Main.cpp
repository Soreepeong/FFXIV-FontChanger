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

HINSTANCE g_hInstance;

int __stdcall WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int nShowCmd) {
	g_hInstance = hInstance;

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

	SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

	std::vector<std::wstring> args;
	if (int nArgs{}; LPWSTR* szArgList = CommandLineToArgvW(GetCommandLineW(), &nArgs)) {
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
