#include "pch.h"
#include "resource.h"
#include "Structs.h"
#include "ExportPreviewWindow.h"
#include "FaceElementEditorDialog.h"
#include "GameFontReloader.h"
#include "MainWindow.h"
#include "ProgressDialog.h"
#include "xivres/textools.h"

enum : uint8_t {
	ListViewColsFamilyName,
	ListViewColsSubfamilyName,
	ListViewColsSize,
	ListViewColsLineHeight,
	ListViewColsAscent,
	ListViewColsHorizontalOffset,
	ListViewColsLetterSpacing,
	ListViewColsGamma,
	ListViewColsCodepoints,
	ListViewColsGlyphCount,
	ListViewColsMergeMode,
	ListViewColsRenderer,
	ListViewColsLookup,
};

static constexpr auto FaceListBoxWidth = 160;
static constexpr auto ListViewHeight = 160;
static constexpr auto EditHeight = 40;

static constexpr GUID Guid_IFileDialog_Json{0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x67}};
static constexpr GUID Guid_IFileDialog_Export{0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x68}};

App::FontEditorWindow::FontEditorWindow(std::vector<std::wstring> args)
	: m_args(std::move(args)) {
	const WNDCLASSEXW wcex{
		.cbSize = sizeof(WNDCLASSEX),
		.style = CS_HREDRAW | CS_VREDRAW,
		.lpfnWndProc = &WndProcInitial,
		.hInstance = g_hInstance,
		.hCursor = LoadCursorW(nullptr, IDC_ARROW),
		.hbrBackground = GetStockBrush(WHITE_BRUSH),
		.lpszMenuName = MAKEINTRESOURCEW(IDR_FONTEDITOR),
		.lpszClassName = ClassName,
	};

	RegisterClassExW(&wcex);

	CreateWindowExW(0, ClassName, L"Font Editor", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
		CW_USEDEFAULT, CW_USEDEFAULT, 1200, 640,
		nullptr, nullptr, nullptr, this);
}

App::FontEditorWindow::~FontEditorWindow() = default;

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

	NONCLIENTMETRICSW ncm = {sizeof(NONCLIENTMETRICSW)};
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

	const auto AddColumn = [this](int columnIndex, int width, const wchar_t* name) {
		LVCOLUMNW col{
			.mask = LVCF_TEXT | LVCF_WIDTH,
			.cx = width,
			.pszText = const_cast<wchar_t*>(name),
		};
		ListView_InsertColumn(m_hFaceElementsListView, columnIndex, &col);
	};
	AddColumn(ListViewColsFamilyName, 120, L"Family");
	AddColumn(ListViewColsSubfamilyName, 80, L"Subfamily");
	AddColumn(ListViewColsSize, 80, L"Size");
	AddColumn(ListViewColsLineHeight, 80, L"Line Height");
	AddColumn(ListViewColsAscent, 80, L"Ascent");
	AddColumn(ListViewColsHorizontalOffset, 120, L"Horizontal Offset");
	AddColumn(ListViewColsLetterSpacing, 100, L"Letter Spacing");
	AddColumn(ListViewColsGamma, 60, L"Gamma");
	AddColumn(ListViewColsCodepoints, 80, L"Codepoints");
	AddColumn(ListViewColsMergeMode, 70, L"Overwrite");
	AddColumn(ListViewColsGlyphCount, 60, L"Glyphs");
	AddColumn(ListViewColsRenderer, 180, L"Renderer");
	AddColumn(ListViewColsLookup, 300, L"Lookup");

	if (m_args.size() >= 2 && std::filesystem::exists(m_args[1])) {
		try {
			SetCurrentMultiFontSet(m_args[1]);
		} catch (const std::exception& e) {
			MessageBoxW(m_hWnd, std::format(L"Failed to open file: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		}
	}
	if (m_path.empty())
		Menu_File_New(xivres::font_type::font);

	Window_OnSize();
	ShowWindow(m_hWnd, SW_SHOW);
	return 0;
}

LRESULT App::FontEditorWindow::Window_OnSize() {
	RECT rc;
	GetClientRect(m_hWnd, &rc);

	auto hdwp = BeginDeferWindowPos(Id_Last_);
	hdwp = DeferWindowPos(hdwp, m_hFacesListBox, nullptr, 0, 0, FaceListBoxWidth, rc.bottom - rc.top, SWP_NOZORDER | SWP_NOACTIVATE);
	hdwp = DeferWindowPos(hdwp, m_hFaceElementsListView, nullptr, FaceListBoxWidth, 0, (std::max<int>)(0, rc.right - rc.left - FaceListBoxWidth), ListViewHeight, SWP_NOZORDER | SWP_NOACTIVATE);
	hdwp = DeferWindowPos(hdwp, m_hEdit, nullptr, FaceListBoxWidth, ListViewHeight, (std::max<int>)(0, rc.right - rc.left - FaceListBoxWidth), EditHeight, SWP_NOZORDER | SWP_NOACTIVATE);
	EndDeferWindowPos(hdwp);

	m_nDrawLeft = FaceListBoxWidth;
	m_nDrawTop = EditHeight + ListViewHeight;
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
		std::ranges::fill(buf, xivres::util::b8g8r8a8{0x88, 0x88, 0x88, 0xFF});

		for (int y = pad; y < m_pMipmap->Height - pad; y++) {
			for (int x = pad; x < m_pMipmap->Width - pad; x++)
				buf[y * m_pMipmap->Width + x] = {0x00, 0x00, 0x00, 0xFF};
		}

		if (m_pActiveFace) {
			auto& face = *m_pActiveFace;

			const auto& mergedFont = *face.GetMergedFont();

			if (int lineHeight = mergedFont.line_height(), ascent = mergedFont.ascent(); lineHeight > 0 && m_bShowLineMetrics) {
				if (ascent < lineHeight) {
					for (int y = pad, y_ = m_pMipmap->Height - pad; y < y_; y += lineHeight) {
						for (int y2 = y + ascent, y2_ = (std::min)(y_, y + lineHeight); y2 < y2_; y2++)
							for (int x = pad; x < m_pMipmap->Width - pad; x++)
								buf[y2 * m_pMipmap->Width + x] = {0x33, 0x33, 0x33, 0xFF};
					}
				} else if (ascent == lineHeight) {
					for (int y = pad, y_ = m_pMipmap->Height - pad; y < y_; y += 2 * lineHeight) {
						for (int y2 = y + lineHeight, y2_ = (std::min)(y_, y + 2 * lineHeight); y2 < y2_; y2++)
							for (int x = pad; x < m_pMipmap->Width - pad; x++)
								buf[y2 * m_pMipmap->Width + x] = {0x33, 0x33, 0x33, 0xFF};
					}
				}
			}

			if (!face.PreviewText.empty()) {
				xivres::fontgen::text_measurer(mergedFont)
					.max_width(m_bWordWrap ? m_pMipmap->Width - pad * 2 : (std::numeric_limits<int>::max)())
					.use_kerning(m_bKerning)
					.measure(face.PreviewText)
					.draw_to(*m_pMipmap, mergedFont, pad, pad, {0xFF, 0xFF, 0xFF, 0xFF}, {0, 0, 0, 0});
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
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bWordWrap ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_VIEW_WORDWRAP, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bKerning ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_VIEW_KERNING, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_bShowLineMetrics ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_VIEW_SHOWLINEMETRICS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_hotReloadFontType == xivres::font_type::undefined ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_HOTRELOAD_FONT_AUTO, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_hotReloadFontType == xivres::font_type::font ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_HOTRELOAD_FONT_FONT, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_hotReloadFontType == xivres::font_type::font_lobby ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_HOTRELOAD_FONT_LOBBY, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_hotReloadFontType == xivres::font_type::chn_axis ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_HOTRELOAD_FONT_CHNAXIS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_hotReloadFontType == xivres::font_type::krn_axis ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_HOTRELOAD_FONT_KRNAXIS, FALSE, &mii);
	}
	{
		const MENUITEMINFOW mii{.cbSize = sizeof mii, .fMask = MIIM_STATE, .fState = static_cast<UINT>(m_multiFontSet.ExportMapFontLobbyToFont ? MFS_CHECKED : 0)};
		SetMenuItemInfoW(hMenu, ID_EXPORT_MAPFONTLOBBY, FALSE, &mii);
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
		SuccessOrThrow(pDialog->SetFileTypes(fileTypesSpan.size(), fileTypesSpan.data()));
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
		SuccessOrThrow(pDialog->SetFileTypes(fileTypesSpan.size(), fileTypesSpan.data()));
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

LRESULT App::FontEditorWindow::Menu_Edit_Add() {
	if (!m_pActiveFace)
		return 0;

	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	std::set<int> indices;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		indices.insert(i);

	const auto count = ListView_GetItemCount(m_hFaceElementsListView);
	if (indices.empty())
		indices.insert(count);

	ListView_SetItemState(m_hFaceElementsListView, -1, 0, LVIS_SELECTED);

	auto& elements = m_pActiveFace->Elements;
	for (const auto pos : indices | std::views::reverse) {
		auto& element = **elements.emplace(elements.begin() + pos, std::make_unique<Structs::FaceElement>());
		if (pos > 0) {
			element = *elements[static_cast<size_t>(pos) - 1];
			element.WrapModifiers.Codepoints.clear();
		}

		LVITEMW lvi{
			.mask = LVIF_PARAM | LVIF_STATE,
			.iItem = pos,
			.state = LVIS_SELECTED,
			.stateMask = LVIS_SELECTED,
			.lParam = reinterpret_cast<LPARAM>(&element),
		};
		ListView_InsertItem(m_hFaceElementsListView, &lvi);
		UpdateFaceElementListViewItem(element);
	}

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	if (indices.size() == 1)
		ShowEditor(*elements[*indices.begin()]);

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Cut() {
	if (Menu_Edit_Copy())
		return 1;

	Menu_Edit_Delete();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Copy() {
	if (!m_pActiveFace)
		return 1;

	auto objs = nlohmann::json::array();
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		objs.emplace_back(*m_pActiveFace->Elements[i]);

	const auto wstr = xivres::util::unicode::convert<std::wstring>(objs.dump());

	const auto clipboard = OpenClipboard(m_hWnd);
	if (!clipboard)
		return 1;
	EmptyClipboard();

	bool copied = false;
	HGLOBAL hg = GlobalAlloc(GMEM_MOVEABLE, (wstr.size() + 1) * 2);
	if (hg) {
		if (const auto pLock = GlobalLock(hg)) {
			memcpy(pLock, wstr.data(), (wstr.size() + 1) * 2);
			copied = SetClipboardData(CF_UNICODETEXT, pLock);
		}
		GlobalUnlock(hg);
		if (!copied)
			GlobalFree(hg);
	}
	CloseClipboard();
	return copied ? 0 : 1;
}

LRESULT App::FontEditorWindow::Menu_Edit_Paste() {
	const auto clipboard = OpenClipboard(m_hWnd);
	if (!clipboard)
		return 0;

	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	std::string data;
	if (const auto pData = GetClipboardData(CF_UNICODETEXT))
		data = xivres::util::unicode::convert<std::string>(static_cast<const wchar_t*>(pData));
	CloseClipboard();

	std::vector<Structs::FaceElement> parsedTemplateElements;
	try {
		const auto parsed = nlohmann::json::parse(data);
		if (!parsed.is_array())
			return 0;
		for (const auto& p : parsed) {
			parsedTemplateElements.emplace_back();
			from_json(p, parsedTemplateElements.back());
		}
		if (parsedTemplateElements.empty())
			return 0;
	} catch (const nlohmann::json::exception&) {
		return 0;
	}

	std::set<int> indices;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		indices.insert(i);

	const auto count = ListView_GetItemCount(m_hFaceElementsListView);
	if (indices.empty())
		indices.insert(count);

	ListView_SetItemState(m_hFaceElementsListView, -1, 0, LVIS_SELECTED);

	auto& elements = m_pActiveFace->Elements;
	for (const auto pos : indices | std::views::reverse) {
		for (const auto& templateElement : parsedTemplateElements | std::views::reverse) {
			auto& element = **elements.emplace(elements.begin() + pos, std::make_unique<Structs::FaceElement>(templateElement));
			LVITEMW lvi{
				.mask = LVIF_PARAM | LVIF_STATE,
				.iItem = pos,
				.state = LVIS_SELECTED,
				.stateMask = LVIS_SELECTED,
				.lParam = reinterpret_cast<LPARAM>(&element),
			};
			ListView_InsertItem(m_hFaceElementsListView, &lvi);
			UpdateFaceElementListViewItem(element);
		}
	}

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Delete() {
	if (!m_pActiveFace)
		return 0;

	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	std::set<int> indices;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		indices.insert(i);
	if (indices.empty())
		return 0;

	for (const auto index : indices | std::views::reverse) {
		ListView_DeleteItem(m_hFaceElementsListView, index);
		m_pActiveFace->Elements.erase(m_pActiveFace->Elements.begin() + index);
	}

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_SelectAll() {
	ListView_SetItemState(m_hFaceElementsListView, -1, LVIS_SELECTED, LVIS_SELECTED);
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Details() {
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		ShowEditor(*m_pActiveFace->Elements[i]);
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_ChangeParams(int baselineShift, int horizontalOffset, int letterSpacing, float fontSize) {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	auto any = false;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));) {
		any = true;
		auto& e = *m_pActiveFace->Elements[i];
		auto baseChanged = false;
		if (e.Renderer == Structs::RendererEnum::Empty) {
			baseChanged |= !!baselineShift;
			baseChanged |= !!(letterSpacing + horizontalOffset);
			e.RendererSpecific.Empty.Ascent += baselineShift;
			e.RendererSpecific.Empty.LineHeight += letterSpacing + horizontalOffset;
		} else {
			e.WrapModifiers.BaselineShift += baselineShift;
			e.WrapModifiers.HorizontalOffset += horizontalOffset;
			e.WrapModifiers.LetterSpacing += letterSpacing;
		}
		if (fontSize != 0.f) {
			e.Size = std::roundf((e.Size + fontSize) * 10.f) / 10.f;
			baseChanged = true;
		}
		if (baseChanged)
			e.OnFontCreateParametersChange();
		else
			e.OnFontWrappingParametersChange();
		UpdateFaceElementListViewItem(e);
	}
	if (!any)
		return 0;

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_ToggleMergeMode() {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	auto any = false;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));) {
		any = true;
		auto& e = *m_pActiveFace->Elements[i];
		e.MergeMode = static_cast<xivres::fontgen::codepoint_merge_mode>((static_cast<int>(e.MergeMode) + 1) % static_cast<int>(xivres::fontgen::codepoint_merge_mode::Enum_Count_));
		e.OnFontWrappingParametersChange();
		UpdateFaceElementListViewItem(e);
	}
	if (!any)
		return 0;

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_MoveUpOrDown(int direction) {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	std::vector<size_t> ids;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		ids.emplace_back(i);

	if (ids.empty())
		return 0;

	std::vector<size_t> allItems;
	allItems.resize(m_pActiveFace->Elements.size());
	for (size_t i = 0; i < allItems.size(); i++)
		allItems[i] = i;

	std::ranges::sort(ids);
	if (direction > 0)
		std::ranges::reverse(ids);

	auto any = false;
	for (const auto& id : ids) {
		if (id + direction < 0 || id + direction >= allItems.size())
			continue;

		any = true;
		std::swap(allItems[id], allItems[id + direction]);
	}
	if (!any)
		return 0;

	std::map<LPARAM, size_t> newLocations;
	for (int i = 0, i_ = static_cast<int>(m_pActiveFace->Elements.size()); i < i_; i++) {
		const LVITEMW lvi{
			.mask = LVIF_PARAM,
			.iItem = i,
		};
		ListView_GetItem(m_hFaceElementsListView, &lvi);
		newLocations[lvi.lParam] = allItems[i];
	}

	const auto listViewSortCallback = [](LPARAM lp1, LPARAM lp2, LPARAM ctx) -> int {
		auto& newLocations = *reinterpret_cast<std::map<LPARAM, size_t>*>(ctx);
		const auto nl = newLocations[lp1];
		const auto nr = newLocations[lp2];
		return nl == nr ? 0 : (nl > nr ? 1 : -1);
	};
	ListView_SortItems(m_hFaceElementsListView, listViewSortCallback, &newLocations);

	std::ranges::sort(m_pActiveFace->Elements, [&newLocations](const auto& l, const auto& r) -> bool {
		const auto nl = newLocations[reinterpret_cast<LPARAM>(l.get())];
		const auto nr = newLocations[reinterpret_cast<LPARAM>(r.get())];
		return nl < nr;
	});

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_CreateEmptyCopyFromSelection() {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	if (!m_pActiveFace)
		return 0;

	const auto refIndex = ListView_GetNextItem(m_hFaceElementsListView, -1, LVNI_SELECTED);
	if (refIndex == -1)
		return 0;

	ListView_SetItemState(m_hFaceElementsListView, -1, 0, LVIS_SELECTED);

	auto& elements = m_pActiveFace->Elements;
	const auto& ref = *elements[refIndex];
	auto& element = **elements.emplace(elements.begin(), std::make_unique<Structs::FaceElement>());
	element.Size = ref.Size;
	element.RendererSpecific = {
		.Empty = {
			.Ascent = ref.GetWrappedFont()->ascent() + ref.WrapModifiers.BaselineShift,
			.LineHeight = ref.GetWrappedFont()->line_height(),
		},
	};

	const LVITEMW lvi{
		.mask = LVIF_PARAM | LVIF_STATE,
		.iItem = 0,
		.state = LVIS_SELECTED,
		.stateMask = LVIS_SELECTED,
		.lParam = reinterpret_cast<LPARAM>(&element),
	};
	ListView_InsertItem(m_hFaceElementsListView, &lvi);
	UpdateFaceElementListViewItem(element);

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_NextOrPrevFont(int direction) {
	auto i = static_cast<size_t>(ListBox_GetCurSel(m_hFacesListBox));
	if (i == static_cast<size_t>(LB_ERR)
		|| i + direction < 0
		|| i + direction >= ListBox_GetCount(m_hFacesListBox))
		return 0;

	i += direction;
	ListBox_SetCurSel(m_hFacesListBox, i);

	for (const auto& pFontSet : m_multiFontSet.FontSets) {
		if (i < pFontSet->Faces.size()) {
			m_pActiveFace = pFontSet->Faces[i].get();
			UpdateFaceElementList();
			break;
		}

		i -= static_cast<int>(pFontSet->Faces.size());
	}
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_WordWrap() {
	m_bWordWrap = !m_bWordWrap;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_Kerning() {
	m_bKerning = !m_bKerning;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_ShowLineMetrics() {
	m_bShowLineMetrics = !m_bShowLineMetrics;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_Zoom(int zoom) {
	m_nZoom = zoom;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_Preview() {
	using namespace xivres::fontgen;

	try {
		ProgressDialog progressDialog(m_hWnd, "Exporting...");
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		std::vector<std::pair<std::string, std::shared_ptr<fixed_size_font>>> resultFonts;
		for (const auto& fontSet : m_multiFontSet.FontSets) {
			const auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *fontSet);

			auto texturesAll = std::make_shared<xivres::texture::stream>(mips[0]->Type, mips[0]->Width, mips[0]->Height, 1, 1, mips.size());
			for (size_t i = 0; i < mips.size(); i++)
				texturesAll->set_mipmap(0, i, mips[i]);

			for (size_t i = 0; i < fdts.size(); i++)
				resultFonts.emplace_back(fontSet->Faces[i]->Name, std::make_shared<fontdata_fixed_size_font>(fdts[i], mips, fontSet->Faces[i]->Name, ""));

			std::thread([texturesAll]() { preview(*texturesAll); }).detach();
		}

		ExportPreviewWindow::ShowNew(std::move(resultFonts));

		return 0;
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}
}

LRESULT App::FontEditorWindow::Menu_Export_Raw() {
	using namespace xivres::fontgen;

	try {
		IFileOpenDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Export));
		SuccessOrThrow(pDialog->SetTitle(L"Export raw"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM | FOS_PICKFOLDERS));
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
		const auto basePath = std::filesystem::path(pszFileName);

		ProgressDialog progressDialog(m_hWnd, "Exporting...");
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		for (const auto& pFontSet : m_multiFontSet.FontSets) {
			const auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *pFontSet);

			progressDialog.UpdateProgress(std::nanf(""));
			progressDialog.UpdateStatusMessage("Writing to files...");

			std::vector<char> buf(32768);
			xivres::texture::stream textureOne(mips[0]->Type, mips[0]->Width, mips[0]->Height, 1, 1, 1);

			for (size_t i = 0; i < mips.size(); i++) {
				progressDialog.ThrowIfCancelled();
				textureOne.set_mipmap(0, 0, mips[i]);

				const auto i1 = i + 1;
				const auto path = basePath / std::vformat(pFontSet->TexFilenameFormat, std::make_format_args(i1));
				std::ofstream out(path, std::ios::binary);

				for (size_t read, pos = 0; (read = textureOne.read(pos, buf.data(), buf.size())); pos += read) {
					progressDialog.ThrowIfCancelled();
					out.write(buf.data(), read);
				}

				out.close();

				if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
					const auto path2 = basePath / std::format("font_lobby{}.tex", i1);
					std::filesystem::copy(path, path2, std::filesystem::copy_options::overwrite_existing);
				}
			}

			for (size_t i = 0; i < fdts.size(); i++) {
				progressDialog.ThrowIfCancelled();
				const auto path = basePath / std::format("{}.fdt", pFontSet->Faces[i]->Name);
				std::ofstream out(path, std::ios::binary);

				for (size_t read, pos = 0; (read = fdts[i]->read(pos, buf.data(), buf.size())); pos += read) {
					progressDialog.ThrowIfCancelled();
					out.write(buf.data(), read);
				}

				out.close();

				if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
					const auto path2 = basePath / std::format("{}_lobby.fdt", pFontSet->Faces[i]->Name);
					std::filesystem::copy(path, path2, std::filesystem::copy_options::overwrite_existing);
				}
			}
		}
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_TTMP(CompressionMode compressionMode) {
	using namespace xivres::fontgen;
	static constexpr COMDLG_FILTERSPEC fileTypes[] = {
		{L"TTMP2 file (*.ttmp2)", L"*.ttmp2"},
		{L"ZIP file (*.zip)", L"*.zip"},
		{L"All files (*.*)", L"*"},
	};
	const auto fileTypesSpan = std::span(fileTypes);

	std::wstring finalPath;
	try {
		IFileSaveDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Export));
		SuccessOrThrow(pDialog->SetFileTypes(fileTypesSpan.size(), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(L"Save"));
		SuccessOrThrow(pDialog->SetFileName(std::format(L"{}.ttmp2", m_path.filename().replace_extension(L"").wstring()).c_str()));
		SuccessOrThrow(pDialog->SetDefaultExtension(L"json"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		{
			IShellItemPtr pResult;
			PWSTR pszFileName;
			SuccessOrThrow(pDialog->GetResult(&pResult));
			SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
			if (!pszFileName)
				throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");

			finalPath = pszFileName;
			CoTaskMemFree(pszFileName);
		}

		xivres::textools::simple_ttmp2_writer writer(finalPath);

		ProgressDialog progressDialog(m_hWnd, "Exporting...");
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		std::vector<std::tuple<std::vector<std::shared_ptr<xivres::fontdata::stream>>, std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>>, const Structs::FontSet&>> pairs;

		writer.begin_packed(compressionMode == CompressionMode::CompressAfterPacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION);
		for (auto& pFontSet : m_multiFontSet.FontSets) {
			auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *pFontSet);

			auto& modsList = writer.ttmpl().SimpleModsList;
			const auto beginIndex = modsList.size();

			for (size_t i = 0; i < fdts.size(); i++) {
				progressDialog.ThrowIfCancelled();

				const auto targetFileName = std::format("common/font/{}.fdt", pFontSet->Faces[i]->Name);
				progressDialog.UpdateStatusMessage(std::format("Packing file: {}", targetFileName));

				writer.add_packed(xivres::compressing_packed_stream<xivres::standard_compressing_packer>(targetFileName, fdts[i], compressionMode == CompressionMode::CompressWhilePacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION));
			}

			for (size_t i = 0; i < mips.size(); i++) {
				progressDialog.ThrowIfCancelled();

				const auto i1 = i + 1;
				const auto targetFileName = std::format("common/font/{}", std::vformat(pFontSet->TexFilenameFormat, std::make_format_args(i1)));
				progressDialog.UpdateStatusMessage(std::format("Packing file: {}", targetFileName));

				const auto& mip = mips[i];
				auto textureOne = std::make_shared<xivres::texture::stream>(mip->Type, mip->Width, mip->Height, 1, 1, 1);
				textureOne->set_mipmap(0, 0, mip);

				writer.add_packed(xivres::compressing_packed_stream<xivres::texture_compressing_packer>(targetFileName, std::move(textureOne), compressionMode == CompressionMode::CompressWhilePacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION));
			}

			const auto endIndex = modsList.size();

			if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
				modsList.reserve(modsList.size() + endIndex - beginIndex);
				for (size_t i = beginIndex; i < endIndex; i++) {
					xivres::textools::mods_json tmp = modsList[i];
					if (tmp.FullPath.ends_with(".fdt")) {
						tmp.Name.erase(tmp.Name.size() - 4, 4);
						tmp.Name.append("_lobby.fdt");
					} else if (tmp.FullPath.ends_with(".tex")) {
						tmp.Name.insert(tmp.Name.size() - 5, "_lobby");
					} else {
						continue;
					}

					tmp.FullPath = xivres::util::unicode::convert<std::string>(tmp.Name, &xivres::util::unicode::lower);
					modsList.push_back(tmp);
				}
			}
		}
		writer.close();
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontLobby() {
	m_multiFontSet.ExportMapFontLobbyToFont = !m_multiFontSet.ExportMapFontLobbyToFont;
	Changes_MarkDirty();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_HotReload_Reload(bool restore) {
	std::vector<DWORD> pids;
	pids.resize(4096);
	DWORD cb;
	EnumProcesses(pids.data(), static_cast<DWORD>(std::span(pids).size_bytes()), &cb);
	pids.resize(cb / sizeof DWORD);
	for (const auto pid : pids) {
		try {
			GameFontReloader::GameProcess game(pid);
			if (restore) {
				game.RefreshFonts(nullptr);
				continue;
			}

			GameFontReloader::FontSet fontSet;
			xivres::font_type baseFontType = m_hotReloadFontType;

			if (baseFontType == xivres::font_type::undefined) {
				if (game.GetFontSets().size() == 5)
					baseFontType = xivres::font_type::chn_axis;
				else if (game.GetFontSets().size() == 4)
					baseFontType = xivres::font_type::krn_axis;
				else
					baseFontType = xivres::font_type::font;

				fontSet = GameFontReloader::GetDefaultFontSet(baseFontType);

				for (const auto& f : m_multiFontSet.FontSets) {
					std::string texNameFormat = f->TexFilenameFormat;
					for (size_t pos; (pos = texNameFormat.find("{}")) != std::string::npos;)
						texNameFormat.replace(pos, 2, "%d");

					for (const auto& face : f->Faces) {
						for (auto& f2 : fontSet.Faces) {
							if (f2.Fdt == face->Name + ".fdt" && f2.TexPattern != texNameFormat) {
								f2.TexPattern = texNameFormat;
								f2.TexCount = f->ExpectedTexCount;
							}
						}
					}
				}
			} else
				fontSet = GameFontReloader::GetDefaultFontSet(baseFontType);

			if (baseFontType == xivres::font_type::chn_axis && game.GetFontSets().size() < 5)
				continue;

			game.RefreshFonts(&fontSet);
		} catch (const std::exception& e) {
			// pass, don't really care
			OutputDebugStringA(std::format("Failed to reload fonts: {}\n", e.what()).c_str());
		}
	}
	return 0;
}

LRESULT App::FontEditorWindow::Menu_HotReload_Font(xivres::font_type mode) {
	m_hotReloadFontType = mode;
	return 0;
}

LRESULT App::FontEditorWindow::Edit_OnCommand(uint16_t commandId) {
	switch (commandId) {
		case EN_CHANGE:
			if (m_pActiveFace) {
				auto& face = *m_pActiveFace;
				face.PreviewText = xivres::util::unicode::convert<std::string>(GetWindowString(m_hEdit));
				Changes_MarkDirty();
				Window_Redraw();
			} else
				return -1;
			return 0;
	}

	return 0;
}

LRESULT App::FontEditorWindow::FaceListBox_OnCommand(uint16_t commandId) {
	switch (commandId) {
		case LBN_SELCHANGE: {
			auto iItem = static_cast<size_t>(ListBox_GetCurSel(m_hFacesListBox));
			if (iItem != static_cast<size_t>(LB_ERR)) {
				for (auto& pFontSet : m_multiFontSet.FontSets) {
					if (iItem < pFontSet->Faces.size()) {
						m_pActiveFace = pFontSet->Faces[iItem].get();
						UpdateFaceElementList();
						break;
					}
					iItem -= static_cast<int>(pFontSet->Faces.size());
				}
			}
			return 0;
		}
	}
	return 0;
}

LRESULT App::FontEditorWindow::FaceElementsListView_OnBeginDrag(NM_LISTVIEW& nmlv) {
	if (!m_pActiveFace)
		return -1;

	m_bIsReorderingFaceElementList = true;
	SetCapture(m_hWnd);
	SetCursor(LoadCursorW(nullptr, IDC_SIZENS));
	return 0;
}

bool App::FontEditorWindow::FaceElementsListView_OnDragProcessMouseUp(int16_t x, int16_t y) {
	if (!m_bIsReorderingFaceElementList)
		return false;

	m_bIsReorderingFaceElementList = false;
	ReleaseCapture();
	FaceElementsListView_DragProcessDragging(x, y);
	return true;
}

bool App::FontEditorWindow::FaceElementsListView_OnDragProcessMouseMove(int16_t x, int16_t y) {
	if (!m_bIsReorderingFaceElementList)
		return false;

	FaceElementsListView_DragProcessDragging(x, y);
	return true;
}

bool App::FontEditorWindow::FaceElementsListView_DragProcessDragging(int16_t x, int16_t y) {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	// Determine the dropped item
	LVHITTESTINFO lvhti{
		.pt = {x, y},
	};
	ClientToScreen(m_hWnd, &lvhti.pt);
	ScreenToClient(m_hFaceElementsListView, &lvhti.pt);
	ListView_HitTest(m_hFaceElementsListView, &lvhti);

	// Out of the ListView?
	if (lvhti.iItem == -1) {
		POINT ptRef{};
		ListView_GetItemPosition(m_hFaceElementsListView, 0, &ptRef);
		if (lvhti.pt.y < ptRef.y)
			lvhti.iItem = 0;
		else {
			RECT rcListView;
			GetClientRect(m_hFaceElementsListView, &rcListView);
			ListView_GetItemPosition(m_hFaceElementsListView, ListView_GetItemCount(m_hFaceElementsListView) - 1, &ptRef);
			if (lvhti.pt.y >= ptRef.y || lvhti.pt.y >= rcListView.bottom - rcListView.top)
				lvhti.iItem = ListView_GetItemCount(m_hFaceElementsListView) - 1;
			else
				return false;
		}
	}

	auto& face = *m_pActiveFace;

	// Rearrange the items
	std::set<int> sourceIndices;
	for (auto iPos = -1; -1 != (iPos = ListView_GetNextItem(m_hFaceElementsListView, iPos, LVNI_SELECTED));)
		sourceIndices.insert(iPos);

	struct SortInfoType {
		std::vector<int> oldIndices;
		std::vector<int> newIndices;
		std::map<LPARAM, int> sourcePtrs;
	} sortInfo;
	sortInfo.oldIndices.reserve(face.Elements.size());
	for (int i = 0, i_ = static_cast<int>(face.Elements.size()); i < i_; i++) {
		LVITEMW lvi{.mask = LVIF_PARAM, .iItem = i};
		ListView_GetItem(m_hFaceElementsListView, &lvi);
		sortInfo.sourcePtrs[lvi.lParam] = i;
		if (!sourceIndices.contains(i))
			sortInfo.oldIndices.push_back(i);
	}

	{
		int i = (std::max<int>)(0, 1 + lvhti.iItem - static_cast<int>(sourceIndices.size()));
		for (const auto sourceIndex : sourceIndices)
			sortInfo.oldIndices.insert(sortInfo.oldIndices.begin() + i++, sourceIndex);
	}

	sortInfo.newIndices.resize(sortInfo.oldIndices.size());
	auto changed = false;
	for (int i = 0, i_ = static_cast<int>(sortInfo.oldIndices.size()); i < i_; i++) {
		changed |= i != sortInfo.oldIndices[i];
		sortInfo.newIndices[sortInfo.oldIndices[i]] = i;
	}

	if (!changed)
		return false;

	const auto listViewSortCallback = [](LPARAM lp1, LPARAM lp2, LPARAM ctx) -> int {
		auto& sortInfo = *reinterpret_cast<SortInfoType*>(ctx);
		const auto il = sortInfo.sourcePtrs[lp1];
		const auto ir = sortInfo.sourcePtrs[lp2];
		const auto nl = sortInfo.newIndices[il];
		const auto nr = sortInfo.newIndices[ir];
		return nl == nr ? 0 : (nl > nr ? 1 : -1);
	};
	ListView_SortItems(m_hFaceElementsListView, listViewSortCallback, &sortInfo);

	std::ranges::sort(face.Elements, [&sortInfo](const auto& l, const auto& r) -> bool {
		const auto il = sortInfo.sourcePtrs[reinterpret_cast<LPARAM>(l.get())];
		const auto ir = sortInfo.sourcePtrs[reinterpret_cast<LPARAM>(r.get())];
		const auto nl = sortInfo.newIndices[il];
		const auto nr = sortInfo.newIndices[ir];
		return nl < nr;
	});

	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();

	return true;
}

LRESULT App::FontEditorWindow::FaceElementsListView_OnDblClick(NMITEMACTIVATE& nmia) {
	if (nmia.iItem == -1)
		return 0;
	if (m_pActiveFace == nullptr)
		return 0;
	if (static_cast<size_t>(nmia.iItem) >= m_pActiveFace->Elements.size())
		return 0;
	ShowEditor(*m_pActiveFace->Elements[nmia.iItem]);
	return 0;
}

void App::FontEditorWindow::SetCurrentMultiFontSet(const std::filesystem::path& path) {
	const auto s = xivres::file_stream(path).read_vector<char>();
	const auto j = nlohmann::json::parse(s.begin(), s.end());
	SetCurrentMultiFontSet(j.get<Structs::MultiFontSet>(), path, false);
}

void App::FontEditorWindow::SetCurrentMultiFontSet(Structs::MultiFontSet multiFontSet, std::filesystem::path path, bool fakePath) {
	m_multiFontSet = std::move(multiFontSet);
	m_path = std::move(path);
	m_bPathIsNotReal = fakePath;

	m_pFontSet = nullptr;
	m_pActiveFace = nullptr;

	UpdateFaceList();
	Changes_MarkFresh();
}

void App::FontEditorWindow::Changes_MarkFresh() {
	m_bChanged = false;

	SetWindowTextW(m_hWnd, std::format(
		L"{} - Font Editor",
		m_path.filename().c_str()
	).c_str());
}

void App::FontEditorWindow::Changes_MarkDirty() {
	if (m_bChanged)
		return;

	m_bChanged = true;

	SetWindowTextW(m_hWnd, std::format(
		L"{} - Font Editor*",
		m_path.filename().c_str()
	).c_str());
}

bool App::FontEditorWindow::Changes_ConfirmIfDirty() {
	if (m_bChanged) {
		switch (MessageBoxW(m_hWnd, L"There are unsaved changes. Do you want to save your changes?", GetWindowString(m_hWnd).c_str(), MB_YESNOCANCEL)) {
			case IDYES:
				if (Menu_File_Save())
					return true;
				break;
			case IDNO:
				break;
			case IDCANCEL:
				return true;
		}
	}
	return false;
}

void App::FontEditorWindow::ShowEditor(Structs::FaceElement& element) {
	auto& pEditorWindow = m_editors[&element];
	if (pEditorWindow && pEditorWindow->IsOpened()) {
		pEditorWindow->Activate();
	} else {
		pEditorWindow = std::make_unique<FaceElementEditorDialog>(m_hWnd, element, [this, &element]() {
			UpdateFaceElementListViewItem(element);
			Changes_MarkDirty();
			m_pActiveFace->OnElementChange();
			Window_Redraw();
		});
	}
}

void App::FontEditorWindow::UpdateFaceList() {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFacesListBox, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFacesListBox, WM_SETREDRAW, TRUE, 0); });

	Structs::Face* currentTag = nullptr;
	if (int curSel = ListBox_GetCurSel(m_hFacesListBox); curSel != LB_ERR)
		currentTag = reinterpret_cast<Structs::Face*>(ListBox_GetItemData(m_hFacesListBox, curSel));

	ListBox_ResetContent(m_hFacesListBox);
	auto selectionRestored = false;

	for (auto& pFontSet : m_multiFontSet.FontSets) {
		for (int i = 0, i_ = static_cast<int>(pFontSet->Faces.size()); i < i_; i++) {
			auto& face = *pFontSet->Faces[i];
			ListBox_AddString(m_hFacesListBox, xivres::util::unicode::convert<std::wstring>(std::format("{}: {}", pFontSet->TexFilenameFormat, face.Name)).c_str());
			ListBox_SetItemData(m_hFacesListBox, i, &face);
			if (currentTag == &face) {
				ListBox_SetCurSel(m_hFacesListBox, i);
				selectionRestored = true;
			}
		}
	}

	if (!selectionRestored) {
		m_pFontSet = nullptr;
		if (!m_multiFontSet.FontSets.empty()) {
			m_pFontSet = m_multiFontSet.FontSets[0].get();
			m_pActiveFace = nullptr;
			if (!m_pFontSet->Faces.empty()) {
				ListBox_SetCurSel(m_hFacesListBox, 0);
				m_pActiveFace = m_pFontSet->Faces[0].get();
			}
		}
	}

	UpdateFaceElementList();
	Window_Redraw();
}

void App::FontEditorWindow::UpdateFaceElementList() {
	const auto tempDisableRedraw = std::shared_ptr<void>(nullptr, [this, _ = SendMessage(m_hFaceElementsListView, WM_SETREDRAW, FALSE, 0)](void*) { SendMessage(m_hFaceElementsListView, WM_SETREDRAW, TRUE, 0); });

	if (!m_pActiveFace) {
		ListView_DeleteAllItems(m_hFaceElementsListView);
		return;
	}

	std::map<LPARAM, size_t> activeElementTags;
	for (const auto& pElement : m_pActiveFace->Elements) {
		const auto& element = *pElement;
		const auto lp = reinterpret_cast<LPARAM>(&element);
		activeElementTags[lp] = activeElementTags.size();
		if (const LVFINDINFOW lvfi{.flags = LVFI_PARAM, .lParam = lp};
			ListView_FindItem(m_hFaceElementsListView, -1, &lvfi) != -1)
			continue;

		LVITEMW lvi{.mask = LVIF_PARAM, .iItem = ListView_GetItemCount(m_hFaceElementsListView), .lParam = lp};
		ListView_InsertItem(m_hFaceElementsListView, &lvi);
		UpdateFaceElementListViewItem(element);
	}

	for (int i = 0, i_ = ListView_GetItemCount(m_hFaceElementsListView); i < i_;) {
		LVITEMW lvi{.mask = LVIF_PARAM, .iItem = i};
		ListView_GetItem(m_hFaceElementsListView, &lvi);
		if (!activeElementTags.contains(lvi.lParam)) {
			i_--;
			ListView_DeleteItem(m_hFaceElementsListView, i);
		} else
			i++;
	}

	const auto listViewSortCallback = [](LPARAM lp1, LPARAM lp2, LPARAM ctx) -> int {
		const auto& activeElementTags = *reinterpret_cast<const std::map<LPARAM, size_t>*>(ctx);
		const auto nl = activeElementTags.at(lp1);
		const auto nr = activeElementTags.at(lp2);
		return nl == nr ? 0 : (nl > nr ? 1 : -1);
	};
	ListView_SortItems(m_hFaceElementsListView, listViewSortCallback, &activeElementTags);

	Edit_SetText(m_hEdit, xivres::util::unicode::convert<std::wstring>(m_pActiveFace->PreviewText).c_str());
	Window_Redraw();
}

void App::FontEditorWindow::UpdateFaceElementListViewItem(const Structs::FaceElement& element) {
	const LVFINDINFOW lvfi{.flags = LVFI_PARAM, .lParam = reinterpret_cast<LPARAM>(&element)};
	const auto index = ListView_FindItem(m_hFaceElementsListView, -1, &lvfi);
	if (index == -1)
		return;

	const auto setItemText = [&](int col, const std::wstring& text) {
		ListView_SetItemText(m_hFaceElementsListView, index, col, const_cast<wchar_t*>(text.c_str()));
	};

	setItemText(ListViewColsFamilyName, xivres::util::unicode::convert<std::wstring>(element.GetWrappedFont()->family_name()));
	setItemText(ListViewColsSubfamilyName, xivres::util::unicode::convert<std::wstring>(element.GetWrappedFont()->subfamily_name()));
	if (std::fabsf(element.GetWrappedFont()->font_size() - element.Size) >= 0.01f) {
		setItemText(ListViewColsSize, std::format(L"{:g}px (req. {:g}px)", element.GetWrappedFont()->font_size(), element.Size));
	} else {
		setItemText(ListViewColsSize, std::format(L"{:g}px", element.GetWrappedFont()->font_size()));
	}
	setItemText(ListViewColsLineHeight, std::format(L"{}px", element.GetWrappedFont()->line_height()));
	if (element.WrapModifiers.BaselineShift && element.Renderer != Structs::RendererEnum::Empty) {
		setItemText(ListViewColsAscent, std::format(L"{}px({:+}px)", element.GetBaseFont()->ascent(), element.WrapModifiers.BaselineShift));
	} else {
		setItemText(ListViewColsAscent, std::format(L"{}px", element.GetBaseFont()->ascent()));
	}
	setItemText(ListViewColsHorizontalOffset, std::format(L"{}px", element.Renderer == Structs::RendererEnum::Empty ? 0 : element.WrapModifiers.HorizontalOffset));
	setItemText(ListViewColsLetterSpacing, std::format(L"{}px", element.Renderer == Structs::RendererEnum::Empty ? 0 : element.WrapModifiers.LetterSpacing));
	setItemText(ListViewColsCodepoints, element.GetRangeRepresentation());
	setItemText(ListViewColsGlyphCount, std::format(L"{}", element.GetWrappedFont()->all_codepoints().size()));
	switch (element.MergeMode) {
		case xivres::fontgen::codepoint_merge_mode::AddNew:
			setItemText(ListViewColsMergeMode, L"Add New");
			break;
		case xivres::fontgen::codepoint_merge_mode::AddAll:
			setItemText(ListViewColsMergeMode, L"Add All");
			break;
		case xivres::fontgen::codepoint_merge_mode::Replace:
			setItemText(ListViewColsMergeMode, L"Replace");
			break;
		default:
			setItemText(ListViewColsMergeMode, L"Invalid");
			break;
	}
	setItemText(ListViewColsGamma, std::format(L"{:g}", element.Gamma));
	setItemText(ListViewColsRenderer, element.GetRendererRepresentation());
	setItemText(ListViewColsLookup, element.GetLookupRepresentation());
}

std::pair<std::vector<std::shared_ptr<xivres::fontdata::stream>>, std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>>> App::FontEditorWindow::CompileCurrentFontSet(ProgressDialog& progressDialog, Structs::FontSet& fontSet) {
	progressDialog.UpdateStatusMessage("Loading base fonts...");
	fontSet.ConsolidateFonts();

	{
		progressDialog.UpdateStatusMessage("Resolving kerning pairs...");
		xivres::util::thread_pool::pool pool(1);
		xivres::util::thread_pool::task_waiter<std::pair<Structs::Face*, size_t>> waiter(pool);
		for (auto& pFace : fontSet.Faces) {
			waiter.submit([pFace = pFace.get(), &progressDialog](auto&) -> std::pair<Structs::Face*, size_t> {
				if (progressDialog.IsCancelled())
					return {pFace, 0};
				return {pFace, pFace->GetMergedFont()->all_kerning_pairs().size()};
			});
		}

		std::vector<std::string> tooManyKernings;
		for (std::optional<std::pair<Structs::Face*, size_t>> res; (res = waiter.get());) {
			const auto& [pFace, nKerns] = *res;
			if (nKerns >= 65536)
				tooManyKernings.emplace_back(std::format("\n{}: {}", pFace->Name, nKerns));
		}
		if (!tooManyKernings.empty()) {
			std::ranges::sort(tooManyKernings);
			std::string s = "The number of kerning entries of the following font(s) exceeds the limit of 65535.";
			for (const auto& s2 : tooManyKernings)
				s += s2;
			throw std::runtime_error(s);
		}
	}
	progressDialog.ThrowIfCancelled();

	xivres::fontgen::fontdata_packer packer;
	packer.set_discard_step(fontSet.DiscardStep);
	packer.set_side_length(fontSet.SideLength);

	for (auto& pFace : fontSet.Faces)
		packer.add_font(pFace->GetMergedFont());

	packer.compile();

	while (!packer.wait(std::chrono::milliseconds(200))) {
		progressDialog.ThrowIfCancelled();

		progressDialog.UpdateStatusMessage(packer.progress_description());
		progressDialog.UpdateProgress(packer.progress_scaled());
	}
	if (const auto err = packer.get_error_if_failed(); !err.empty())
		throw std::runtime_error(err);

	const auto& fdts = packer.compiled_fontdatas();
	const auto& mips = packer.compiled_mipmap_streams();
	if (mips.empty())
		throw std::runtime_error("No mipmap produced");

	if (fontSet.ExpectedTexCount != static_cast<int>(mips.size())) {
		fontSet.ExpectedTexCount = static_cast<int>(mips.size());
		Changes_MarkDirty();
	}
	return std::make_pair(fdts, mips);
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
