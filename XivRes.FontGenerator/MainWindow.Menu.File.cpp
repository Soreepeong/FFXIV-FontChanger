#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "xivres/textools.h"

LRESULT App::FontEditorWindow::Menu_File_New(xivres::font_type fontType) {
	if (Changes_ConfirmIfDirty())
		return 1;

	Structs::MultiFontSet mfs;

	switch (fontType) {
		case xivres::font_type::font:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 7;
			SetCurrentMultiFontSet(std::move(mfs), "Untitled (font)", true);
			break;
		case xivres::font_type::font_lobby:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 6;
			SetCurrentMultiFontSet(std::move(mfs), "Untitled (font_lobby)", true);
			break;
		case xivres::font_type::chn_axis:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 20;
			SetCurrentMultiFontSet(std::move(mfs), "Untitled (chn_axis)", true);
			break;
		case xivres::font_type::krn_axis:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 9;
			SetCurrentMultiFontSet(std::move(mfs), "Untitled (krn_axis)", true);
			break;
		default:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			SetCurrentMultiFontSet(std::move(mfs), "Untitled", true);
			break;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Open() {
	using namespace xivres::fontgen;
	static constexpr COMDLG_FILTERSPEC fileTypes[] = {
		{L"Preset JSON Files (*.json)", L"*.json"},
		{L"All files (*.*)", L"*"},
	};
	const auto fileTypesSpan = std::span(fileTypes);

	if (Changes_ConfirmIfDirty())
		return 1;

	try {
		IFileOpenDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Json));
		SuccessOrThrow(pDialog->SetFileTypes(static_cast<UINT>(fileTypesSpan.size()), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(L"Open"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		PWSTR pszFileName;
		SuccessOrThrow(pDialog->GetResult(&pResult));
		SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
		if (!pszFileName)
			throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");

		std::unique_ptr<std::remove_pointer_t<PWSTR>, decltype(&CoTaskMemFree)> pszFileNamePtr(pszFileName, &CoTaskMemFree);

		SetCurrentMultiFontSet(pszFileName);
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to open file: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Save() {
	if (m_path.empty() || m_bPathIsNotReal)
		return Menu_File_SaveAs(true);

	try {
		const auto dump = nlohmann::json(m_multiFontSet).dump(1, '\t');
		std::ofstream(m_path, std::ios::binary).write(dump.data(), dump.size());
		Changes_MarkFresh();
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to save file: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_SaveAs(bool changeCurrentFile) {
	using namespace xivres::fontgen;
	static constexpr COMDLG_FILTERSPEC fileTypes[] = {
		{L"Preset JSON Files (*.json)", L"*.json"},
		{L"All files (*.*)", L"*"},
	};
	const auto fileTypesSpan = std::span(fileTypes);

	try {
		const auto dump = nlohmann::json(m_multiFontSet).dump(1, '\t');

		IFileSaveDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Json));
		SuccessOrThrow(pDialog->SetFileTypes(static_cast<UINT>(fileTypesSpan.size()), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(L"Save"));
		SuccessOrThrow(pDialog->SetDefaultExtension(L"json"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		PWSTR pszFileName;
		SuccessOrThrow(pDialog->GetResult(&pResult));
		SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
		if (!pszFileName)
			throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");

		std::unique_ptr<std::remove_pointer_t<PWSTR>, decltype(&CoTaskMemFree)> pszFileNamePtr(pszFileName, &CoTaskMemFree);

		std::ofstream(pszFileName, std::ios::binary).write(dump.data(), dump.size());
		m_path = pszFileName;
		m_bPathIsNotReal = false;
		Changes_MarkFresh();
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to save file: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Exit() {
	if (Changes_ConfirmIfDirty())
		return 1;

	DestroyWindow(m_hWnd);
	return 0;
}
