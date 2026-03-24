#include "pch.h"
#include "NegativeBearingCodepointsDialog.h"
#include "resource.h"

struct App::NegativeBearingCodepointsDialog::ControlStruct {
	HWND Window;
	HWND List = GetDlgItem(Window, IDC_LIST_NEGATIVEBEARING);
};

enum : uint8_t {
	ColCodepoint,
	ColCharacter,
	ColX1,
};

void App::NegativeBearingCodepointsDialog::Show(HWND hParentWnd, std::vector<std::pair<char32_t, int>> entries) {
	auto res = FindResourceExW(g_hInstance, RT_DIALOG, MAKEINTRESOURCEW(IDD_NEGATIVEBEARINGCODEPOINTS), g_langId);
	if (!res)
		res = FindResourceW(g_hInstance, MAKEINTRESOURCEW(IDD_NEGATIVEBEARINGCODEPOINTS), RT_DIALOG);
	std::unique_ptr<std::remove_pointer_t<HGLOBAL>, decltype(&FreeResource)> hglob(LoadResource(g_hInstance, res), &FreeResource);

	const auto dlg = new NegativeBearingCodepointsDialog(std::move(entries));
	CreateDialogIndirectParamW(
		g_hInstance,
		static_cast<DLGTEMPLATE*>(LockResource(hglob.get())),
		hParentWnd,
		DlgProcStatic,
		reinterpret_cast<LPARAM>(dlg));
	ShowWindow(dlg->m_hWnd, SW_SHOW);
}

App::NegativeBearingCodepointsDialog::NegativeBearingCodepointsDialog(std::vector<std::pair<char32_t, int>> entries)
	: m_entries(std::move(entries)) {
}

App::NegativeBearingCodepointsDialog::~NegativeBearingCodepointsDialog() {
	delete m_controls;
}

INT_PTR App::NegativeBearingCodepointsDialog::Dialog_OnInitDialog() {
	m_controls = new ControlStruct{ m_hWnd };

	ListView_SetExtendedListViewStyle(m_controls->List, LVS_EX_FULLROWSELECT);

	const auto zoom = GetZoomFromWindow(m_hWnd);
	const auto AddColumn = [&](int col, int cx, UINT resId) {
		std::wstring name(GetStringResource(resId));
		LVCOLUMNW lvc{ .mask = LVCF_TEXT | LVCF_WIDTH, .cx = static_cast<int>(cx * zoom), .pszText = name.data() };
		ListView_InsertColumn(m_controls->List, col, &lvc);
	};
	AddColumn(ColCodepoint, 80,  IDS_NEGATIVEBEARING_COLUMN_CODEPOINT);
	AddColumn(ColCharacter, 100, IDS_NEGATIVEBEARING_COLUMN_CHARACTER);
	AddColumn(ColX1,        60,  IDS_NEGATIVEBEARING_COLUMN_X1);

	for (int i = 0; i < static_cast<int>(m_entries.size()); ++i) {
		const auto [cp, x1] = m_entries[i];

		auto cpText = std::format(L"U+{:04X}", static_cast<unsigned>(cp));
		LVITEMW lvi{ .mask = LVIF_TEXT, .iItem = i, .iSubItem = 0, .pszText = cpText.data() };
		ListView_InsertItem(m_controls->List, &lvi);

		std::wstring charText;
		if (cp >= U' ' && cp != 0x7F) {
			if (cp < 0x10000) {
				charText += static_cast<wchar_t>(cp);
			} else {
				const auto reduced = cp - 0x10000u;
				charText += static_cast<wchar_t>(0xD800 + (reduced >> 10));
				charText += static_cast<wchar_t>(0xDC00 + (reduced & 0x3FF));
			}
		}
		ListView_SetItemText(m_controls->List, i, ColCharacter, charText.data());

		auto x1Text = std::format(L"{}", x1);
		ListView_SetItemText(m_controls->List, i, ColX1, x1Text.data());
	}

	return TRUE;
}

INT_PTR App::NegativeBearingCodepointsDialog::DlgProc(UINT message, WPARAM wParam, LPARAM lParam) {
	switch (message) {
		case WM_INITDIALOG:
			return Dialog_OnInitDialog();
		case WM_COMMAND:
			switch (LOWORD(wParam)) {
				case IDOK:
				case IDCANCEL:
					DestroyWindow(m_hWnd);
					return TRUE;
			}
			break;
		case WM_DESTROY:
			delete this;
			return TRUE;
	}
	return FALSE;
}

INT_PTR __stdcall App::NegativeBearingCodepointsDialog::DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
	NegativeBearingCodepointsDialog* pThis;
	if (message == WM_INITDIALOG) {
		pThis = reinterpret_cast<NegativeBearingCodepointsDialog*>(lParam);
		pThis->m_hWnd = hwnd;
		SetWindowLongPtrW(hwnd, DWLP_USER, reinterpret_cast<LONG_PTR>(pThis));
	} else {
		pThis = reinterpret_cast<NegativeBearingCodepointsDialog*>(GetWindowLongPtrW(hwnd, DWLP_USER));
	}
	if (!pThis)
		return FALSE;
	return pThis->DlgProc(message, wParam, lParam);
}
