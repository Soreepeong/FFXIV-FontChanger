#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "xivres/textools.h"
#include "resource.h"
#include "FontGeneratorConfig.h"

LRESULT App::FontEditorWindow::Menu_File_New(xivres::font_type fontType) {
	if (Changes_ConfirmIfDirty())
		return 1;

	Structs::MultiFontSet mfs;

	switch (fontType) {
		case xivres::font_type::font:
		default:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 7;
			break;
		case xivres::font_type::font_lobby:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 6;
			break;
		case xivres::font_type::chn_axis:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 20;
			break;
		case xivres::font_type::krn_axis:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 9;
			break;
	}

	SetCurrentMultiFontSet(std::move(mfs), nullptr, true);
	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Open() {
	using namespace xivres::fontgen;

	const auto presetJsonFilesName = std::wstring(GetStringResource(IDS_FILTERSPEC_PRESETJSONFILES));
	const auto allFilesName = std::wstring(GetStringResource(IDS_FILTERSPEC_ALLFILES));
	COMDLG_FILTERSPEC fileTypes[] = {
		{presetJsonFilesName.c_str(), L"*.json"},
		{allFilesName.c_str(), L"*"},
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
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_OPEN)).c_str()));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		SuccessOrThrow(pDialog->GetResult(&pResult));
		SetCurrentMultiFontSet(std::move(pResult));
	} catch (const WException& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_OPENFILEFAILURE_BODY),
				e.what()).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_OPENFILEFAILURE_BODY),
				xivres::util::unicode::convert<std::wstring>(e.what())).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Save() {
	if (!m_currentShellItem)
		return Menu_File_SaveAs(true);

	try {
		const auto dump = nlohmann::json(m_multiFontSet).dump(1, '\t');

		IStreamPtr strm;
		SuccessOrThrow(m_currentShellItem->BindToHandler(nullptr, BHID_Stream, IID_IStream, reinterpret_cast<void**>(&strm)));

		SuccessOrThrow(strm->SetSize({}));
		SuccessOrThrow(strm->Seek({}, SEEK_SET, nullptr));

		for (std::span remaining(dump); !remaining.empty();) {
			ULONG written;
			SuccessOrThrow(strm->Write(remaining.data(), static_cast<ULONG>((std::min<size_t>)(remaining.size(), 0x10000000)), &written));
			remaining = remaining.subspan(written);
		}

		strm.Release();

		Changes_MarkFresh();
	} catch (const WException& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_SAVEFILEFAILURE_BODY),
				e.what()).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_SAVEFILEFAILURE_BODY),
				xivres::util::unicode::convert<std::wstring>(e.what())).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_SaveAs(bool changeCurrentFile) {
	using namespace xivres::fontgen;
	const auto presetJsonFilesName = std::wstring(GetStringResource(IDS_FILTERSPEC_PRESETJSONFILES));
	const auto allFilesName = std::wstring(GetStringResource(IDS_FILTERSPEC_ALLFILES));
	COMDLG_FILTERSPEC fileTypes[] = {
		{presetJsonFilesName.c_str(), L"*.json"},
		{allFilesName.c_str(), L"*"},
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
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_SAVE)).c_str()));
		SuccessOrThrow(pDialog->SetFileName(std::filesystem::path(GetCurrentFileName()).c_str()));
		SuccessOrThrow(pDialog->SetDefaultExtension(L"json"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		SuccessOrThrow(pDialog->GetResult(&pResult));

		IStreamPtr strm;
		SuccessOrThrow(pResult->BindToHandler(nullptr, BHID_Stream, IID_IStream, reinterpret_cast<void**>(&strm)));

		SuccessOrThrow(strm->SetSize({}));
		SuccessOrThrow(strm->Seek({}, SEEK_SET, nullptr));

		for (std::span remaining(dump); !remaining.empty();) {
			ULONG written;
			SuccessOrThrow(strm->Write(remaining.data(), static_cast<ULONG>((std::min<size_t>)(remaining.size(), 0x10000000)), &written));
			if (!written)
				throw std::system_error(std::error_code(ERROR_HANDLE_EOF, std::system_category()));
			remaining = remaining.subspan(written);
		}

		strm.Release();

		if (changeCurrentFile) {
			m_currentShellItem = std::move(pResult);
			Changes_MarkFresh();
		}
	} catch (const WException& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_SAVEFILEFAILURE_BODY),
				e.what()).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(
			m_hWnd,
			std::format(
				L"{}\n\n{}",
				GetStringResource(IDS_ERROR_SAVEFILEFAILURE_BODY),
				xivres::util::unicode::convert<std::wstring>(e.what())).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Language(const char* language) {
	if (g_config.Language != language) {
		g_config.Language = language;
		g_config.Save();

		WORD langId = *language ? LANGIDFROMLCID(LocaleNameToLCID(xivres::util::unicode::convert<std::wstring>(language).c_str(), LOCALE_ALLOW_NEUTRAL_NAMES)) : 0;
		MessageBoxW(
			nullptr,
			std::wstring(GetStringResource(IDS_LANGUAGE_RESTARTREQUIRED, langId)).c_str(),
			std::wstring(GetStringResource(IDS_APP, langId)).c_str(),
			MB_OK);
	}
	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Exit() {
	if (Changes_ConfirmIfDirty())
		return 1;

	DestroyWindow(m_hWnd);
	return 0;
}
