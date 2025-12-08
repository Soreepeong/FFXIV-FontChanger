#include "pch.h"

#include "FaceElementEditorDialog.h"
#include "resource.h"
#include "Structs.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "FontGeneratorConfig.h"

bool App::FontEditorWindow::ConsumeDialogMessage(MSG& msg) {
	if (IsDialogMessage(m_hWnd, &msg))
		return true;

	for (const auto& e : m_editors | std::views::values)
		if (e && e->IsOpened() && e->ConsumeDialogMessage(msg))
			return true;

	return false;
}

bool App::FontEditorWindow::ConsumeAccelerator(MSG& msg) {
	if (!m_hAccelerator)
		return false;

	if (GetForegroundWindow() != m_hWnd)
		return false;

	if (msg.message == WM_KEYDOWN && msg.hwnd == m_hEdit) {
		if (msg.wParam == VK_RETURN || msg.wParam == VK_INSERT || msg.wParam == VK_DELETE)
			return false;
		if (!(GetKeyState(VK_CONTROL) & 0x8000) || msg.wParam == 'C' || msg.wParam == 'X' || msg.wParam == 'V' || msg.wParam == 'A')
			return false;
	}
	return TranslateAccelerator(m_hWnd, m_hAccelerator, &msg);
}

LRESULT App::FontEditorWindow::Window_OnCreate(HWND hwnd) {
	m_hWnd = hwnd;

	m_hAccelerator = LoadAcceleratorsW(g_hInstance, MAKEINTRESOURCEW(IDR_ACCELERATOR_FACEELEMENTEDITOR));

	auto hRes = FindResourceExW(g_hInstance, RT_MENU, MAKEINTRESOURCEW(IDR_FONTEDITOR), g_langId);
	if (!hRes)
		hRes = FindResourceW(g_hInstance, MAKEINTRESOURCEW(IDR_FONTEDITOR), RT_MENU);
	if (!hRes)
		throw std::system_error(std::error_code(GetLastError(), std::system_category()));

	const auto hGlob = LoadResource(g_hInstance, hRes);
	if (!hGlob)
		throw std::system_error(std::error_code(GetLastError(), std::system_category()));
	SetMenu(m_hWnd, LoadMenuIndirectW(LockResource(hGlob)));
	FreeResource(hGlob);

	NONCLIENTMETRICSW ncm = { sizeof(NONCLIENTMETRICSW) };
	SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof ncm, &ncm, 0);
	m_hUiFont = CreateFontIndirectW(&ncm.lfMessageFont);

	m_hFacesListBox = CreateWindowExW(0, WC_LISTBOXW, nullptr,
		WS_CHILD | WS_TABSTOP | WS_BORDER | WS_VISIBLE | LBS_NOINTEGRALHEIGHT | LBS_NOTIFY,
		0, 0, 0, 0, m_hWnd, reinterpret_cast<HMENU>(Id_FaceListBox), reinterpret_cast<HINSTANCE>(GetWindowLongPtrW(m_hWnd, GWLP_HINSTANCE)), nullptr);
	m_hFaceElementsListView = CreateWindowExW(0, WC_LISTVIEWW, nullptr,
		WS_CHILD | WS_TABSTOP | WS_BORDER | WS_VISIBLE | LVS_REPORT | LVS_SHOWSELALWAYS,
		0, 0, 0, 0, m_hWnd, reinterpret_cast<HMENU>(Id_FaceElementListView), reinterpret_cast<HINSTANCE>(GetWindowLongPtrW(m_hWnd, GWLP_HINSTANCE)), nullptr);
	m_hEdit = CreateWindowExW(0, WC_EDITW, nullptr,
		WS_CHILD | WS_TABSTOP | WS_BORDER | WS_VISIBLE | WS_VSCROLL | ES_LEFT | ES_AUTOVSCROLL | ES_MULTILINE | ES_WANTRETURN,
		0, 0, 0, 0, m_hWnd, reinterpret_cast<HMENU>(Id_Edit), reinterpret_cast<HINSTANCE>(GetWindowLongPtrW(m_hWnd, GWLP_HINSTANCE)), nullptr);

	ListView_SetExtendedListViewStyle(m_hFaceElementsListView, LVS_EX_FULLROWSELECT);

	SendMessage(m_hFacesListBox, WM_SETFONT, reinterpret_cast<WPARAM>(m_hUiFont), FALSE);
	SendMessage(m_hFaceElementsListView, WM_SETFONT, reinterpret_cast<WPARAM>(m_hUiFont), FALSE);
	SendMessage(m_hEdit, WM_SETFONT, reinterpret_cast<WPARAM>(m_hUiFont), FALSE);

	SetWindowSubclass(m_hEdit, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
		if (msg == WM_GETDLGCODE && wParam == VK_TAB)
			return 0;
		if (msg == WM_KEYDOWN && wParam == 'A' && (GetKeyState(VK_CONTROL) & 0x8000) && !(GetKeyState(VK_SHIFT) & 0x8000) && !(GetKeyState(VK_MENU) & 0x8000) && !(GetKeyState(VK_LWIN) & 0x8000) && !(GetKeyState(VK_RWIN) & 0x8000))
			Edit_SetSel(hWnd, 0, Edit_GetTextLength(hWnd));
		return DefSubclassProc(hWnd, msg, wParam, lParam);
		}, 1, 0);

	const auto AddColumn = [this, zoom = GetZoom()](int columnIndex, int width, UINT resId) {
		std::wstring name(GetStringResource(resId));
		LVCOLUMNW col{
			.mask = LVCF_TEXT | LVCF_WIDTH,
			.cx = static_cast<int>(width * zoom),
			.pszText = const_cast<wchar_t*>(name.c_str()),
		};
		ListView_InsertColumn(m_hFaceElementsListView, columnIndex, &col);
		};
	AddColumn(ListViewColsFamilyName, 120, IDS_FONTLISTVIEW_COLUMN_FAMILY);
	AddColumn(ListViewColsSubfamilyName, 80, IDS_FONTLISTVIEW_COLUMN_SUBFAMILY);
	AddColumn(ListViewColsSize, 80, IDS_FONTLISTVIEW_COLUMN_SIZE);
	AddColumn(ListViewColsLineHeight, 80, IDS_FONTLISTVIEW_COLUMN_LINE_HEIGHT);
	AddColumn(ListViewColsAscent, 80, IDS_FONTLISTVIEW_COLUMN_ASCENT);
	AddColumn(ListViewColsHorizontalOffset, 120, IDS_FONTLISTVIEW_COLUMN_HORIZONTAL_OFFSET);
	AddColumn(ListViewColsLetterSpacing, 100, IDS_FONTLISTVIEW_COLUMN_LETTER_SPACING);
	AddColumn(ListViewColsGamma, 60, IDS_FONTLISTVIEW_COLUMN_GAMMA);
	AddColumn(ListViewColsCodepoints, 80, IDS_FONTLISTVIEW_COLUMN_CODEPOINTS);
	AddColumn(ListViewColsMergeMode, 70, IDS_FONTLISTVIEW_COLUMN_OVERWRITE);
	AddColumn(ListViewColsGlyphCount, 60, IDS_FONTLISTVIEW_COLUMN_GLYPHS);
	AddColumn(ListViewColsRenderer, 180, IDS_FONTLISTVIEW_COLUMN_RENDERER);
	AddColumn(ListViewColsLookup, 300, IDS_FONTLISTVIEW_COLUMN_LOOKUP);

	if (m_args.size() >= 2 && std::filesystem::exists(m_args[1])) {
		try {
			IShellItemPtr shellItem;
			SuccessOrThrow(SHCreateItemFromParsingName(m_args[1].c_str(), nullptr, IID_PPV_ARGS(&shellItem)));
			SetCurrentMultiFontSet(std::move(shellItem));
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
	}
	if (!m_currentShellItem)
		Menu_File_New(xivres::font_type::font);

	Window_OnSize();
	ShowWindow(m_hWnd, SW_SHOW);
	return 0;
}

LRESULT App::FontEditorWindow::Window_OnSize() {
	RECT rc;
	GetClientRect(m_hWnd, &rc);

	const auto zoom = GetZoom();
	const auto scaledFaceListBoxWidth = static_cast<int>(FaceListBoxWidth * zoom);
	const auto scaledListViewHeight = static_cast<int>(ListViewHeight * zoom);
	const auto scaledEditHeight = static_cast<int>(EditHeight * zoom);

	auto hdwp = BeginDeferWindowPos(Id_Last_);
	hdwp = DeferWindowPos(
		hdwp,
		m_hFacesListBox,
		nullptr,
		0,
		0,
		scaledFaceListBoxWidth,
		rc.bottom - rc.top,
		SWP_NOZORDER | SWP_NOACTIVATE);
	hdwp = DeferWindowPos(
		hdwp,
		m_hFaceElementsListView,
		nullptr,
		scaledFaceListBoxWidth,
		0,
		(std::max<int>)(0, rc.right - rc.left - scaledFaceListBoxWidth),
		scaledListViewHeight,
		SWP_NOZORDER | SWP_NOACTIVATE);
	hdwp = DeferWindowPos(
		hdwp,
		m_hEdit,
		nullptr,
		scaledFaceListBoxWidth,
		scaledListViewHeight,
		(std::max<int>)(0, rc.right - rc.left - scaledFaceListBoxWidth),
		scaledEditHeight,
		SWP_NOZORDER | SWP_NOACTIVATE);
	EndDeferWindowPos(hdwp);

	m_nDrawLeft = scaledFaceListBoxWidth;
	m_nDrawTop = scaledEditHeight + scaledListViewHeight;
	m_bNeedRedraw = true;

	return 0;
}

LRESULT App::FontEditorWindow::Window_OnPaint() {
	union {
		char bmibuf[sizeof(BITMAPINFOHEADER) + sizeof(DWORD) * 3];
		BITMAPINFO bmi{};
	};

	PAINTSTRUCT ps;
	const auto hdc = BeginPaint(m_hWnd, &ps);
	if (m_bNeedRedraw) {
		m_bNeedRedraw = false;

		RECT rc;
		GetClientRect(m_hWnd, &rc);
		m_pMipmap = std::make_shared<xivres::texture::memory_mipmap_stream>(
			(std::max<int>)(1, (rc.right - rc.left - m_nDrawLeft + m_nZoom - 1) / m_nZoom),
			(std::max<int>)(1, (rc.bottom - rc.top - m_nDrawTop + m_nZoom - 1) / m_nZoom),
			1,
			xivres::texture::formats::B8G8R8A8);

		const auto pad = 16 / m_nZoom;
		const auto buf = m_pMipmap->as_span<xivres::util::b8g8r8a8>();
		std::ranges::fill(buf, xivres::util::b8g8r8a8{ 0x88, 0x88, 0x88, 0xFF });

		for (int y = pad; y < m_pMipmap->Height - pad; y++) {
			for (int x = pad; x < m_pMipmap->Width - pad; x++)
				buf[y * m_pMipmap->Width + x] = { 0x00, 0x00, 0x00, 0xFF };
		}

		if (m_pActiveFace) {
			auto& face = *m_pActiveFace;

			const auto& mergedFont = *face.GetMergedFont();

			if (int lineHeight = mergedFont.line_height(), ascent = mergedFont.ascent(); lineHeight > 0 && m_bShowLineMetrics) {
				if (ascent < lineHeight) {
					for (int y = pad, y_ = m_pMipmap->Height - pad; y < y_; y += lineHeight) {
						for (int y2 = y + ascent, y2_ = (std::min)(y_, y + lineHeight); y2 < y2_; y2++)
							for (int x = pad; x < m_pMipmap->Width - pad; x++)
								buf[y2 * m_pMipmap->Width + x] = { 0x33, 0x33, 0x33, 0xFF };
					}
				} else if (ascent == lineHeight) {
					for (int y = pad, y_ = m_pMipmap->Height - pad; y < y_; y += 2 * lineHeight) {
						for (int y2 = y + lineHeight, y2_ = (std::min)(y_, y + 2 * lineHeight); y2 < y2_; y2++)
							for (int x = pad; x < m_pMipmap->Width - pad; x++)
								buf[y2 * m_pMipmap->Width + x] = { 0x33, 0x33, 0x33, 0xFF };
					}
				}
			}

			if (!face.PreviewText.empty()) {
				xivres::fontgen::text_measurer(mergedFont)
					.max_width(m_bWordWrap ? m_pMipmap->Width - pad * 2 : (std::numeric_limits<int>::max)())
					.use_kerning(m_bKerning)
					.measure(face.PreviewText)
					.draw_to(*m_pMipmap, mergedFont, pad, pad, { 0xFF, 0xFF, 0xFF, 0xFF }, { 0, 0, 0, 0 });
			}
		}
	}

	bmi.bmiHeader.biSize = sizeof bmi.bmiHeader;
	bmi.bmiHeader.biWidth = m_pMipmap->Width;
	bmi.bmiHeader.biHeight = -m_pMipmap->Height;
	bmi.bmiHeader.biPlanes = 1;
	bmi.bmiHeader.biBitCount = 32;
	bmi.bmiHeader.biCompression = BI_BITFIELDS;
	reinterpret_cast<xivres::util::b8g8r8a8*>(&bmi.bmiColors[0])->set_components(255, 0, 0, 0);
	reinterpret_cast<xivres::util::b8g8r8a8*>(&bmi.bmiColors[1])->set_components(0, 255, 0, 0);
	reinterpret_cast<xivres::util::b8g8r8a8*>(&bmi.bmiColors[2])->set_components(0, 0, 255, 0);
	RECT rc;
	GetClientRect(m_hWnd, &rc);
	StretchDIBits(hdc, m_nDrawLeft, m_nDrawTop, m_pMipmap->Width * m_nZoom, m_pMipmap->Height * m_nZoom, 0, 0, m_pMipmap->Width, m_pMipmap->Height, m_pMipmap->as_span<xivres::util::b8g8r8a8>().data(), &bmi, DIB_RGB_COLORS, SRCCOPY);
	EndPaint(m_hWnd, &ps);

	return 0;
}

LRESULT App::FontEditorWindow::Window_OnInitMenuPopup(HMENU hMenu, int index, bool isWindowMenu) {
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(g_config.Language == "" ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_FILE_LANGUAGE_AUTO, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(g_config.Language == "en-us" ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_FILE_LANGUAGE_ENGLISH, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(g_config.Language == "ko-kr" ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_FILE_LANGUAGE_KOREAN, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bWordWrap ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_VIEW_WORDWRAP, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bKerning ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_VIEW_KERNING, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bShowLineMetrics ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_VIEW_SHOWLINEMETRICS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_multiFontSet.ExportMapFontLobbyToFont ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_EXPORT_MAPFONTLOBBY, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_multiFontSet.ExportMapChnAxisToFont ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_EXPORT_MAPFONTCHNAXIS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_multiFontSet.ExportMapKrnAxisToFont ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_EXPORT_MAPFONTKRNAXIS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{ .cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_multiFontSet.ExportMapKrnAxisToFont ? MFS_CHECKED : 0) };
		SetMenuItemInfoW(hMenu, ID_EXPORT_MAPFONTTCAXIS, FALSE, &mii);
	}
	return 0;
}

LRESULT App::FontEditorWindow::Window_OnMouseMove(uint16_t states, int16_t x, int16_t y) {
	if (FaceElementsListView_OnDragProcessMouseMove(x, y))
		return 0;

	return 0;
}

LRESULT App::FontEditorWindow::Window_OnMouseLButtonUp(uint16_t states, int16_t x, int16_t y) {
	if (FaceElementsListView_OnDragProcessMouseUp(x, y))
		return 0;

	return 0;
}

LRESULT App::FontEditorWindow::Window_OnDestroy() {
	DeleteFont(m_hUiFont);
	PostQuitMessage(0);
	return 0;
}

void App::FontEditorWindow::Window_Redraw() {
	if (!m_pActiveFace)
		return;

	m_bNeedRedraw = true;
	InvalidateRect(m_hWnd, nullptr, FALSE);
}

LRESULT App::FontEditorWindow::WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
	switch (msg) {
		case WM_COMMAND:
			switch (LOWORD(wParam)) {
				case Id_Edit: return Edit_OnCommand(HIWORD(wParam));
				case Id_FaceListBox: return FaceListBox_OnCommand(HIWORD(wParam));
				case ID_FILE_NEW_MAINGAMEFONT: return Menu_File_New(xivres::font_type::font);
				case ID_FILE_NEW_LOBBYFONT: return Menu_File_New(xivres::font_type::font_lobby);
				case ID_FILE_NEW_CHNAXIS: return Menu_File_New(xivres::font_type::chn_axis);
		        case ID_FILE_NEW_TCAXIS: return Menu_File_New(xivres::font_type::tc_axis);
				case ID_FILE_NEW_KRNAXIS: return Menu_File_New(xivres::font_type::krn_axis);
				case ID_FILE_OPEN: return Menu_File_Open();
				case ID_FILE_SAVE: return Menu_File_Save();
				case ID_FILE_SAVEAS: return Menu_File_SaveAs(true);
				case ID_FILE_SAVECOPYAS: return Menu_File_SaveAs(false);
				case ID_FILE_LANGUAGE_AUTO: return Menu_File_Language("");
				case ID_FILE_LANGUAGE_ENGLISH: return Menu_File_Language("en-us");
				case ID_FILE_LANGUAGE_KOREAN: return Menu_File_Language("ko-kr");
				case ID_FILE_EXIT: return Menu_File_Exit();
				case ID_EDIT_ADD: return Menu_Edit_Add();
				case ID_EDIT_CUT: return Menu_Edit_Cut();
				case ID_EDIT_COPY: return Menu_Edit_Copy();
				case ID_EDIT_PASTE: return Menu_Edit_Paste();
				case ID_EDIT_DELETE: return Menu_Edit_Delete();
				case ID_EDIT_SELECTALL: return Menu_Edit_SelectAll();
				case ID_EDIT_DETAILS: return Menu_Edit_Details();
				case ID_EDIT_DECREASEBASELINESHIFT: return Menu_Edit_ChangeParams(-1, 0, 0, 0);
				case ID_EDIT_INCREASEBASELINESHIFT: return Menu_Edit_ChangeParams(+1, 0, 0, 0);
				case ID_EDIT_DECREASEHORIZONTALOFFSET: return Menu_Edit_ChangeParams(0, -1, 0, 0);
				case ID_EDIT_INCREASEHORIZONTALOFFSET: return Menu_Edit_ChangeParams(0, +1, 0, 0);
				case ID_EDIT_DECREASELETTERSPACING: return Menu_Edit_ChangeParams(0, 0, -1, 0);
				case ID_EDIT_INCREASELETTERSPACING: return Menu_Edit_ChangeParams(0, 0, +1, 0);
				case ID_EDIT_DECREASEFONTSIZEBY1: return Menu_Edit_ChangeParams(0, 0, 0, -1.f);
				case ID_EDIT_INCREASEFONTSIZEBY1: return Menu_Edit_ChangeParams(0, 0, 0, +1.f);
				case ID_EDIT_DECREASEFONTSIZEBY0_2: return Menu_Edit_ChangeParams(0, 0, 0, -0.2f);
				case ID_EDIT_INCREASEFONTSIZEBY0_2: return Menu_Edit_ChangeParams(0, 0, 0, +0.2f);
				case ID_EDIT_TOGGLEMERGEMODE: return Menu_Edit_ToggleMergeMode();
				case ID_EDIT_MOVEUP: return Menu_Edit_MoveUpOrDown(-1);
				case ID_EDIT_MOVEDOWN: return Menu_Edit_MoveUpOrDown(+1);
				case ID_EDIT_CREATEEMPTYCOPYFROMSELECTION: return Menu_Edit_CreateEmptyCopyFromSelection();
				case ID_VIEW_PREVIOUSFONT: return Menu_View_NextOrPrevFont(-1);
				case ID_VIEW_NEXTFONT: return Menu_View_NextOrPrevFont(1);
				case ID_VIEW_WORDWRAP: return Menu_View_WordWrap();
				case ID_VIEW_KERNING: return Menu_View_Kerning();
				case ID_VIEW_SHOWLINEMETRICS: return Menu_View_ShowLineMetrics();
				case ID_VIEW_100: return Menu_View_Zoom(1);
				case ID_VIEW_200: return Menu_View_Zoom(2);
				case ID_VIEW_300: return Menu_View_Zoom(3);
				case ID_VIEW_400: return Menu_View_Zoom(4);
				case ID_VIEW_500: return Menu_View_Zoom(5);
				case ID_VIEW_600: return Menu_View_Zoom(6);
				case ID_VIEW_700: return Menu_View_Zoom(7);
				case ID_VIEW_800: return Menu_View_Zoom(8);
				case ID_VIEW_900: return Menu_View_Zoom(9);
				case ID_EXPORT_PREVIEW: return Menu_Export_Preview();
				case ID_EXPORT_RAW: return Menu_Export_Raw();
				case ID_EXPORT_TOTTMP_COMPRESSWHILEPACKING: return Menu_Export_TTMP(CompressionMode::CompressWhilePacking);
				case ID_EXPORT_TOTTMP_COMPRESSAFTERPACKING: return Menu_Export_TTMP(CompressionMode::CompressAfterPacking);
				case ID_EXPORT_TOTTMP_DONOTCOMPRESS: return Menu_Export_TTMP(CompressionMode::DoNotCompress);
				case ID_EXPORT_MAPFONTLOBBY: return Menu_Export_MapFontLobby();
				case ID_EXPORT_MAPFONTCHNAXIS: return Menu_Export_MapFontChnAxis();
				case ID_EXPORT_MAPFONTKRNAXIS: return Menu_Export_MapFontKrnAxis();
			    case ID_EXPORT_MAPFONTTCAXIS: return Menu_Export_MapFontTCAxis();
			}
			break;

		case WM_NOTIFY:
			switch (auto& hdr = *reinterpret_cast<NMHDR*>(lParam); hdr.idFrom) {
				case Id_FaceElementListView:
					switch (hdr.code) {
						case LVN_BEGINDRAG: return FaceElementsListView_OnBeginDrag(*(reinterpret_cast<NM_LISTVIEW*>(lParam)));
						case NM_DBLCLK: return FaceElementsListView_OnDblClick(*(reinterpret_cast<NMITEMACTIVATE*>(lParam)));
					}
					break;
			}
			return 0;

		case WM_CREATE: return Window_OnCreate(hwnd);
		case WM_MOUSEMOVE: return Window_OnMouseMove(static_cast<uint16_t>(wParam), LOWORD(lParam), HIWORD(lParam));
		case WM_LBUTTONUP: return Window_OnMouseLButtonUp(static_cast<uint16_t>(wParam), LOWORD(lParam), HIWORD(lParam));
		case WM_SIZE: return Window_OnSize();
		case WM_PAINT: return Window_OnPaint();
		case WM_INITMENUPOPUP: return Window_OnInitMenuPopup(reinterpret_cast<HMENU>(wParam), LOWORD(lParam), !!HIWORD(lParam));
		case WM_CLOSE: return Menu_File_Exit();
		case WM_DESTROY: return Window_OnDestroy();
	}

	return DefWindowProcW(hwnd, msg, wParam, lParam);
}

LRESULT WINAPI App::FontEditorWindow::WndProcStatic(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
	return reinterpret_cast<FontEditorWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA))->WndProc(hwnd, msg, wParam, lParam);
}

LRESULT WINAPI App::FontEditorWindow::WndProcInitial(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
	if (msg != WM_NCCREATE)
		return DefWindowProcW(hwnd, msg, wParam, lParam);

	const auto pCreateStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
	const auto pImpl = static_cast<FontEditorWindow*>(pCreateStruct->lpCreateParams);
	SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(pImpl));
	SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(WndProcStatic));

	return pImpl->WndProc(hwnd, msg, wParam, lParam);
}
