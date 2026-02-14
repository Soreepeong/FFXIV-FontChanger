#include "pch.h"
#include "GameInstallationManagerDialog.h"
#include "resource.h"

static constexpr GUID Guid_IFileDialog_GameExecutablePath{ 0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x69} };

struct App::GameInstallationManagerDialog::ControlStruct {
	HWND Window;
	HWND OkButton = GetDlgItem(Window, IDOK);
	HWND CancelButton = GetDlgItem(Window, IDCANCEL);
	HWND PathList = GetDlgItem(Window, IDC_PATHLIST);
	HWND DetectButton = GetDlgItem(Window, IDC_DETECT);
	HWND AddButton = GetDlgItem(Window, IDC_ADD);
	HWND RemoveButton = GetDlgItem(Window, IDC_REMOVE);
};

enum : uint8_t {
	ListViewColsPath,
	ListViewColsVendor,
	ListViewColsExists,
};

App::GameInstallationManagerDialog::Installation::Installation(std::filesystem::path path, GameReleaseVendor vendor)
	: Path(path)
	, Vendor(vendor) {
	try {
		Exists = exists(path);
	} catch (...) {
		Exists = false;
	}
}

std::optional<FontGeneratorConfig> App::GameInstallationManagerDialog::Show(HWND hParentWnd, const FontGeneratorConfig& config) {
	auto res = FindResourceExW(g_hInstance, RT_DIALOG, MAKEINTRESOURCEW(IDD_GAMEINSTALLATIONMANAGER), g_langId);
	if (!res)
		res = FindResourceW(g_hInstance, MAKEINTRESOURCEW(IDD_GAMEINSTALLATIONMANAGER), RT_DIALOG);
	std::unique_ptr<std::remove_pointer_t<HGLOBAL>, decltype(&FreeResource)> hglob(LoadResource(g_hInstance, res), &FreeResource);

	GameInstallationManagerDialog dlg(hParentWnd, config);
	const auto r = reinterpret_cast<FontGeneratorConfig*>(DialogBoxIndirectParamW(
		g_hInstance,
		static_cast<DLGTEMPLATE*>(LockResource(hglob.get())),
		hParentWnd,
		DlgProcStatic,
		reinterpret_cast<LPARAM>(&dlg)));
	if (!r)
		return std::nullopt;

	FontGeneratorConfig newConfig(*r);
	delete r;
	return newConfig;
}

App::GameInstallationManagerDialog::GameInstallationManagerDialog(HWND hParentWnd, const FontGeneratorConfig& config)
	: m_hParentWnd(hParentWnd)
	, m_prevConfig(config) {
}

App::GameInstallationManagerDialog::~GameInstallationManagerDialog() {
	delete m_controls;
}

INT_PTR App::GameInstallationManagerDialog::Dialog_OnInitDialog() {
	ListView_SetExtendedListViewStyle(m_controls->PathList, LVS_EX_FULLROWSELECT);

	const auto AddColumn = [this, zoom = GetZoomFromWindow(m_hWnd)](int columnIndex, int width, UINT resId) {
		std::wstring name(GetStringResource(resId));
		LVCOLUMNW col{
			.mask = LVCF_TEXT | LVCF_WIDTH,
			.cx = static_cast<int>(width * zoom),
			.pszText = const_cast<wchar_t*>(name.c_str()),
		};
		ListView_InsertColumn(m_controls->PathList, columnIndex, &col);
		};
	AddColumn(ListViewColsPath, 180, IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_PATH);
	AddColumn(ListViewColsVendor, 80, IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_VENDOR);
	AddColumn(ListViewColsExists, 80, IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_EXISTS);

	for (const auto& p : m_prevConfig.Global)
		AddInstallation(p, GameReleaseVendor::SquareEnix);
	for (const auto& p : m_prevConfig.China)
		AddInstallation(p, GameReleaseVendor::ShandaGames);
	for (const auto& p : m_prevConfig.Korea)
		AddInstallation(p, GameReleaseVendor::ActozSoft);
	for (const auto& p : m_prevConfig.TraditionalChinese)
		AddInstallation(p, GameReleaseVendor::UserjoyGames);

	return 0;
}

INT_PTR App::GameInstallationManagerDialog::OkButton_OnCommand(uint16_t notiCode) {
	auto newConfig = new FontGeneratorConfig(m_prevConfig);
	newConfig->Global.clear();
	newConfig->China.clear();
	newConfig->Korea.clear();
	newConfig->TraditionalChinese.clear();
	for (const auto& installation : m_installations) {
		switch (installation.Vendor) {
			case GameReleaseVendor::SquareEnix:
				newConfig->Global.emplace_back(installation.Path);
				break;
			case GameReleaseVendor::ShandaGames:
				newConfig->China.emplace_back(installation.Path);
				break;
			case GameReleaseVendor::ActozSoft:
				newConfig->Korea.emplace_back(installation.Path);
				break;
			case GameReleaseVendor::UserjoyGames:
				newConfig->TraditionalChinese.emplace_back(installation.Path);
				break;
		}
	}
	EndDialog(m_controls->Window, reinterpret_cast<INT_PTR>(newConfig));
	return 0;
}

INT_PTR App::GameInstallationManagerDialog::CancelButton_OnCommand(uint16_t notiCode) {
	EndDialog(m_controls->Window, 0);
	return 0;
}

INT_PTR App::GameInstallationManagerDialog::DetectButton_OnCommand(uint16_t notiCode) {
	auto found = 0;
	for (auto& [region, path] : GameInstallationRepository::AutoDetectInstalledGameReleases()) {
		if (std::ranges::all_of(m_installations, [&](const auto& item) { return item.Path != path; })) {
			AddInstallation(std::move(path), region);
			found++;
		}
	}

	switch (found) {
		case 0:
			MessageBoxW(
				m_hWnd,
				std::wstring(GetStringResource(IDS_GAMEINSTALLATIONDETECT_FOUND_0)).c_str(),
				GetWindowString(m_hWnd).c_str(),
				MB_OK | MB_ICONWARNING);
			break;
		case 1:
			MessageBoxW(
				m_hWnd,
				std::wstring(GetStringResource(IDS_GAMEINSTALLATIONDETECT_FOUND_1)).c_str(),
				GetWindowString(m_hWnd).c_str(),
				MB_OK | MB_ICONINFORMATION);
			break;
		default:
			MessageBoxW(
				m_hWnd,
				std::vformat(GetStringResource(IDS_GAMEINSTALLATIONDETECT_FOUND_N), std::make_wformat_args(found)).c_str(),
				GetWindowString(m_hWnd).c_str(),
				MB_OK | MB_ICONINFORMATION);
			break;
	}

	return INT_PTR();
}

INT_PTR App::GameInstallationManagerDialog::AddButton_OnCommand(uint16_t notiCode) {
	using namespace xivres::fontgen;

	const auto allFilesName = std::wstring(GetStringResource(IDS_FILTERSPEC_ALLFILES));
	COMDLG_FILTERSPEC fileTypes[] = {
		{L"ffxiv.exe, ffxiv_dx11.exe", L"ffxiv.exe;ffxiv_dx11.exe"},
		{allFilesName.c_str(), L"*"},
	};
	const auto fileTypesSpan = std::span(fileTypes);

	try {
		IFileOpenDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_GameExecutablePath));
		SuccessOrThrow(pDialog->SetFileTypes(static_cast<UINT>(fileTypesSpan.size()), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_OPEN)).c_str()));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), { HRESULT_FROM_WIN32(ERROR_CANCELLED) })) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		SuccessOrThrow(pDialog->GetResult(&pResult));

		LPWSTR pszName;
		SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszName));

		std::filesystem::path path;
		const auto vendor = GameInstallationRepository::DetermineGameRelease(pszName, path);
		if (vendor == GameReleaseVendor::None) {
			MessageBoxW(
				m_hWnd,
				std::wstring(GetStringResource(IDS_GAMEINSTALLATIONADD_NOTAGAME)).c_str(),
				GetWindowString(m_hWnd).c_str(),
				MB_OK | MB_ICONWARNING);
			return 1;
		}

		AddInstallation(std::move(path), vendor);
	} catch (const WException& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_OPENFILEFAILURE_BODY, e);
		return 1;
	} catch (const std::system_error& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_OPENFILEFAILURE_BODY, e);
		return 1;
	} catch (const std::exception& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_OPENFILEFAILURE_BODY, e);
		return 1;
	}

	return 0;
}

INT_PTR App::GameInstallationManagerDialog::RemoveButton_OnCommand(uint16_t notiCode) {
	const auto count = ListView_GetSelectedCount(m_controls->PathList);
	if (!count)
		return 0;
	
	std::vector<int> vecIndices;
	std::vector<int> listIndices;
	vecIndices.reserve(count);
	listIndices.reserve(count);
	for (int i = -1; (i = ListView_GetNextItem(m_controls->PathList, i, LVNI_SELECTED)) != -1;) {
		LVITEMW lvi{ .mask = LVIF_PARAM, .iItem = i };
		ListView_GetItem(m_controls->PathList, &lvi);
		vecIndices.emplace_back(static_cast<int>(lvi.lParam));
		listIndices.emplace_back(i);
	}

	std::ranges::sort(vecIndices);
	std::ranges::sort(listIndices);

	for (const auto i : std::ranges::reverse_view(vecIndices))
		m_installations.erase(m_installations.begin() + i);

	for (const auto i : std::ranges::reverse_view(listIndices))
		ListView_DeleteItem(m_controls->PathList, i);

	EnableWindow(m_controls->RemoveButton, !!ListView_GetSelectedCount(m_controls->PathList));

	return 0;
}

INT_PTR App::GameInstallationManagerDialog::PathListView_ItemChanged(const NMLISTVIEW& nm) {
	EnableWindow(m_controls->RemoveButton, !!ListView_GetSelectedCount(m_controls->PathList));
	return 0;
}

INT_PTR App::GameInstallationManagerDialog::PathListView_ColumnClick(const NMLISTVIEW& nm) {
	if (nm.iSubItem == m_sortCol) {
		m_sortAscending = !m_sortAscending;
	} else {
		m_sortCol = nm.iSubItem;
		m_sortAscending = true;
	}

	ListView_SortItems(nm.hdr.hwndFrom, [](LPARAM lp1, LPARAM lp2, LPARAM sortParam) -> int {
		auto& t = *reinterpret_cast<GameInstallationManagerDialog*>(sortParam);
		const auto m = t.m_sortAscending ? 1 : -1;
		switch (t.m_sortCol) {
			case ListViewColsPath:
				return m * _wcsicmp(t.m_installations[lp1].Path.c_str(), t.m_installations[lp2].Path.c_str());
			case ListViewColsVendor:
				return m * (static_cast<int>(t.m_installations[lp1].Vendor) - static_cast<int>(t.m_installations[lp2].Vendor));
			case ListViewColsExists:
				return m * (static_cast<int>(t.m_installations[lp1].Exists) - static_cast<int>(t.m_installations[lp2].Exists));
			default:
				return 0;
		}
		}, this);
	return 0;
}

INT_PTR App::GameInstallationManagerDialog::PathListView_DoubleClick(const NMITEMACTIVATE& nm) {
	if (nm.lParam >= 0 && nm.lParam < m_installations.size())
		ShellExecuteW(m_hWnd, L"explore", m_installations[static_cast<size_t>(nm.lParam)].Path.c_str(), nullptr, nullptr, SW_SHOW);
	
	return 0;
}

void App::GameInstallationManagerDialog::AddInstallation(std::filesystem::path path, GameReleaseVendor vendor) {
	const auto i = static_cast<int>(m_installations.size());
	const auto& inst = m_installations.emplace_back(path, vendor);
	LVITEMW lvi{ .mask = LVIF_PARAM, .iItem = i, .lParam = static_cast<LPARAM>(i) };
	ListView_InsertItem(m_controls->PathList, &lvi);
	ListView_SetItemText(m_controls->PathList, i, ListViewColsPath, const_cast<wchar_t*>(inst.Path.c_str()));

	std::wstring tmp;
	switch (inst.Vendor) {
		case GameReleaseVendor::SquareEnix:
			tmp = GetStringResource(IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_VENDOR_SQEX);
			break;
		case GameReleaseVendor::ShandaGames:
			tmp = GetStringResource(IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_VENDOR_SNDA);
			break;
		case GameReleaseVendor::ActozSoft:
			tmp = GetStringResource(IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_VENDOR_ACTOZ);
			break;
		case GameReleaseVendor::UserjoyGames:
			tmp = GetStringResource(IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_VENDOR_USERJOY);
			break;
	}

	ListView_SetItemText(m_controls->PathList, i, ListViewColsVendor, const_cast<wchar_t*>(tmp.c_str()));

	tmp = GetStringResource(inst.Exists
		? IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_EXISTS_YES
		: IDS_GAMEINSTALLATIONLISTVIEW_COLUMN_EXISTS_NO);
	ListView_SetItemText(m_controls->PathList, i, ListViewColsExists, const_cast<wchar_t*>(tmp.c_str()));
}

INT_PTR App::GameInstallationManagerDialog::DlgProc(UINT message, WPARAM wParam, LPARAM lParam) {
	switch (message) {
		case WM_INITDIALOG:
			return Dialog_OnInitDialog();
		case WM_COMMAND: {
			switch (LOWORD(wParam)) {
				case IDOK: return OkButton_OnCommand(HIWORD(wParam));
				case IDCANCEL: return CancelButton_OnCommand(HIWORD(wParam));
				case IDC_DETECT: return DetectButton_OnCommand(HIWORD(wParam));
				case IDC_ADD: return AddButton_OnCommand(HIWORD(wParam));
				case IDC_REMOVE: return RemoveButton_OnCommand(HIWORD(wParam));
			}
			return 0;
		}
		case WM_NOTIFY: {
			const auto& nmhdr = *reinterpret_cast<LPNMHDR>(lParam);
			switch (nmhdr.idFrom) {
				case IDC_PATHLIST: {
					switch (nmhdr.code) {
						case LVN_ITEMCHANGED: return PathListView_ItemChanged(*reinterpret_cast<LPNMLISTVIEW>(lParam));
						case LVN_COLUMNCLICK: return PathListView_ColumnClick(*reinterpret_cast<LPNMLISTVIEW>(lParam));
						case NM_DBLCLK: return PathListView_DoubleClick(*reinterpret_cast<LPNMITEMACTIVATE>(lParam));
					}
					return 0;
				}
			}
			return 0;
		}

		case WM_CLOSE: {
			EndDialog(m_controls->Window, 0);
			return 0;
		}
		case WM_DESTROY: {
			return 0;
		}
	}
	return 0;
}

INT_PTR __stdcall App::GameInstallationManagerDialog::DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
	if (message == WM_INITDIALOG) {
		auto& params = *reinterpret_cast<GameInstallationManagerDialog*>(lParam);
		params.m_hWnd = hwnd;
		params.m_controls = new ControlStruct{ hwnd };
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&params));
		return params.DlgProc(message, wParam, lParam);
	} else {
		return reinterpret_cast<GameInstallationManagerDialog*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA))->DlgProc(message, wParam, lParam);
	}
	return 0;
}
