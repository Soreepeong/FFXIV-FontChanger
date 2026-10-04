#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "xivres/textools.h"
#include "resource.h"
#include "FileHistory.h"
#include "FontGeneratorConfig.h"
#include "GameInstallationManagerDialog.h"

#include <Shlwapi.h>

#pragma comment(lib, "shlwapi.lib")

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
		case xivres::font_type::tc_axis:
			mfs.FontSets.emplace_back(std::make_unique<Structs::FontSet>(Structs::FontSet::NewFromTemplateFont(fontType)));
			mfs.FontSets.back()->ExpectedTexCount = 20;
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

	return TryCatchShowError(m_hWnd, IDS_ERROR_OPENFILEFAILURE_BODY, LRESULT{1}, [&]() -> LRESULT {
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
		return 0;
	});
}

LRESULT App::FontEditorWindow::Menu_File_Save() {
	if (!m_currentShellItem)
		return Menu_File_SaveAs(true);

	return TryCatchShowError(m_hWnd, IDS_ERROR_SAVEFILEFAILURE_BODY, LRESULT{1}, [&]() -> LRESULT {
		const auto dump = nlohmann::json(m_multiFontSet).dump(1, '\t');

		IBindCtxPtr bindCtx;
		SuccessOrThrow(CreateBindCtx(0, &bindCtx));

		BIND_OPTS bindOpts{
			.cbStruct = sizeof bindOpts,
			.grfMode = STGM_WRITE | STGM_SHARE_EXCLUSIVE | STGM_CREATE,
		};
		SuccessOrThrow(bindCtx->SetBindOptions(&bindOpts));

		IStreamPtr strm;
		SuccessOrThrow(m_currentShellItem->BindToHandler(bindCtx, BHID_Stream, IID_IStream, reinterpret_cast<void**>(&strm)));

		for (std::span remaining(dump); !remaining.empty();) {
			ULONG written;
			SuccessOrThrow(strm->Write(remaining.data(), static_cast<ULONG>((std::min<size_t>)(remaining.size(), 0x10000000)), &written));
			remaining = remaining.subspan(written);
		}

		strm.Release();

		Changes_MarkFresh();
		FileHistory::Add(m_currentShellItem.GetInterfacePtr());
		return 0;
	});
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

	return TryCatchShowError(m_hWnd, IDS_ERROR_SAVEFILEFAILURE_BODY, LRESULT{1}, [&]() -> LRESULT {
		const auto dump = nlohmann::json(m_multiFontSet).dump(1, '\t');

		IBindCtxPtr bindCtx;
		SuccessOrThrow(CreateBindCtx(0, &bindCtx));

		BIND_OPTS bindOpts{
			.cbStruct = sizeof bindOpts,
			.grfMode = STGM_WRITE | STGM_SHARE_EXCLUSIVE | STGM_CREATE,
		};
		SuccessOrThrow(bindCtx->SetBindOptions(&bindOpts));

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
		SuccessOrThrow(pResult->BindToHandler(bindCtx, BHID_Stream, IID_IStream, reinterpret_cast<void**>(&strm)));

		for (std::span remaining(dump); !remaining.empty();) {
			ULONG written;
			SuccessOrThrow(strm->Write(remaining.data(), static_cast<ULONG>((std::min<size_t>)(remaining.size(), 0x10000000)), &written));
			if (!written)
				throw std::system_error(std::error_code(ERROR_HANDLE_EOF, std::system_category()));
			remaining = remaining.subspan(written);
		}

		strm.Release();

		FileHistory::Add(pResult.GetInterfacePtr());
		if (changeCurrentFile) {
			m_currentShellItem = std::move(pResult);
			Changes_MarkFresh();
		}
		return 0;
	});
}

void App::FontEditorWindow::PopulateRecentFilesMenu(HMENU hMenu) {
	// Remove recent items from the last time
	for (auto i = GetMenuItemCount(hMenu); i-- > 0;) {
		MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_ID};
		if (GetMenuItemInfoW(hMenu, i, TRUE, &mii) && mii.wID >= ID_FILE_RECENT_CLEAR && mii.wID <= ID_FILE_RECENT_LAST)
			DeleteMenu(hMenu, i, MF_BYPOSITION);
	}

	const auto history = FileHistory::Load();
	if (history.Files.empty())
		return;

	// The files go above the separator before Exit, as a group of their own.
	auto position = 0;
	for (auto i = 0, i_ = GetMenuItemCount(hMenu); i < i_; i++) {
		if (GetMenuItemID(hMenu, i) == ID_FILE_EXIT)
			position = (std::max)(0, i - 1);
	}
	const auto insert = [&](UINT type, UINT id, const wchar_t* text) {
		const MENUITEMINFOW mii{
			.cbSize = sizeof mii,
			.fMask = MIIM_FTYPE | MIIM_ID | (text ? MIIM_STRING : 0u),
			.fType = type,
			.wID = id,
			.dwTypeData = const_cast<wchar_t*>(text),
		};
		InsertMenuItemW(hMenu, position++, TRUE, &mii);
	};

	insert(MFT_SEPARATOR, ID_FILE_RECENT_SEPARATOR, nullptr);
	for (size_t i = 0; i < history.Files.size() && i <= ID_FILE_RECENT_LAST - ID_FILE_RECENT_FIRST; i++) {
		// Long paths are shortened in the middle; ampersands in paths are not mnemonics.
		wchar_t compact[MAX_PATH]{};
		if (!PathCompactPathExW(compact, history.Files[i].c_str(), 64, 0))
			wcsncpy_s(compact, history.Files[i].c_str(), _TRUNCATE);
		std::wstring escaped;
		for (const auto c : std::wstring_view(compact)) {
			if (c == L'&')
				escaped += L'&';
			escaped += c;
		}

		const auto label = i < 9 ? std::format(L"&{} {}", i + 1, escaped) : std::format(L"1&0 {}", escaped);
		insert(MFT_STRING, ID_FILE_RECENT_FIRST + static_cast<UINT>(i), label.c_str());
	}
	insert(MFT_STRING, ID_FILE_RECENT_CLEAR, std::wstring(GetStringResource(IDS_RECENTFILES_CLEAR)).c_str());
}

LRESULT App::FontEditorWindow::Menu_File_OpenRecent(size_t index) {
	const auto history = FileHistory::Load();
	if (index >= history.Files.size())
		return 0;

	const auto path = history.Files[index];
	if (!std::filesystem::exists(path)) {
		if (MessageBoxW(
			m_hWnd,
			std::format(L"{}\n\n{}", path.wstring(), std::wstring(GetStringResource(IDS_RECENTFILES_NOTFOUND))).c_str(),
			std::wstring(GetStringResource(IDS_APP)).c_str(),
			MB_YESNO | MB_ICONWARNING) == IDYES)
			FileHistory::Remove(path);
		return 0;
	}

	if (Changes_ConfirmIfDirty())
		return 1;

	return TryCatchShowError(m_hWnd, IDS_ERROR_OPENFILEFAILURE_BODY, LRESULT{1}, [&]() -> LRESULT {
		IShellItemPtr shellItem;
		SuccessOrThrow(SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(&shellItem)));
		SetCurrentMultiFontSet(std::move(shellItem));
		return 0;
	});
}

LRESULT App::FontEditorWindow::Menu_File_ClearRecent() {
	FileHistory::Clear();
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

LRESULT App::FontEditorWindow::Menu_File_GameInstallationManager() {
	if (auto newConf = GameInstallationManagerDialog::Show(m_hWnd, g_config); newConf.has_value()) {
		g_config.Global = std::move(newConf->Global);
		g_config.China = std::move(newConf->China);
		g_config.Korea = std::move(newConf->Korea);
		g_config.TraditionalChinese = std::move(newConf->TraditionalChinese);
		g_config.Save();
		Structs::FlushCachedFonts();
		this->m_multiFontSet.FlushCache();
		Window_Redraw();
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_File_Exit() {
	if (Changes_ConfirmIfDirty())
		return 1;

	DestroyWindow(m_hWnd);
	return 0;
}
