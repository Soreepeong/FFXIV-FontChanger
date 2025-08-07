#include "pch.h"
#include "resource.h"
#include "Structs.h"
#include "FaceElementEditorDialog.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "ProgressDialog.h"
#include "xivres/textools.h"

App::FontEditorWindow::FontEditorWindow(std::vector<std::wstring> args)
	: m_args(std::move(args)) {
	const WNDCLASSEXW wcex{
		.cbSize = sizeof(WNDCLASSEX),
		.style = CS_HREDRAW | CS_VREDRAW,
		.lpfnWndProc = &WndProcInitial,
		.hInstance = g_hInstance,
		.hCursor = LoadCursorW(nullptr, IDC_ARROW),
		.hbrBackground = GetStockBrush(WHITE_BRUSH),
		.lpszMenuName = nullptr,
		.lpszClassName = ClassName,
	};

	RegisterClassExW(&wcex);

	if (!CreateWindowExW(0, ClassName, L"Font Editor", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
		CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT,
		nullptr, nullptr, nullptr, this))
		throw std::system_error(std::error_code(GetLastError(), std::system_category()));
}

App::FontEditorWindow::~FontEditorWindow() = default;

void App::FontEditorWindow::SetCurrentMultiFontSet(IShellItemPtr path) {
	IBindCtxPtr bindCtx;
	SuccessOrThrow(CreateBindCtx(0, &bindCtx));

	BIND_OPTS bindOpts{
		.cbStruct = sizeof bindOpts,
		.grfMode = STGM_READ | STGM_SHARE_DENY_WRITE,
	};
	SuccessOrThrow(bindCtx->SetBindOptions(&bindOpts));

	IStreamPtr stream;
	SuccessOrThrow(path->BindToHandler(bindCtx, BHID_Stream, IID_PPV_ARGS(&stream)));

	STATSTG stat;
	SuccessOrThrow(stream->Stat(&stat, STATFLAG_NONAME));
	std::vector<char> buf(stat.cbSize.QuadPart);

	for (std::span remaining(buf); !remaining.empty();) {
		ULONG read;
		SuccessOrThrow(stream->Read(buf.data(), static_cast<ULONG>((std::min<size_t>)(buf.size(), 0x10000000)), &read));
		if (!read)
			throw std::system_error(std::error_code(ERROR_HANDLE_EOF, std::system_category()));
		remaining = remaining.subspan(read);
	}
	const auto j = nlohmann::json::parse(buf.begin(), buf.end());
	SetCurrentMultiFontSet(j.get<Structs::MultiFontSet>(), std::move(path), false);
}

void App::FontEditorWindow::SetCurrentMultiFontSet(Structs::MultiFontSet multiFontSet, IShellItemPtr path, bool fakePath) {
	m_multiFontSet = std::move(multiFontSet);
	m_currentShellItem = std::move(path);

	m_pFontSet = nullptr;
	m_pActiveFace = nullptr;

	UpdateFaceList();
	Changes_MarkFresh();
}

std::wstring App::FontEditorWindow::GetCurrentFileName() {
	std::wstring fileName(GetStringResource(IDS_FILENAME_UNTITLED));
	if (m_currentShellItem) {
		PWSTR pszFileName;
		SuccessOrThrow(m_currentShellItem->GetDisplayName(SIGDN_NORMALDISPLAY, &pszFileName));
		if (pszFileName)
			fileName = pszFileName;
		CoTaskMemFree(pszFileName);
	}

	return fileName;
}

void App::FontEditorWindow::Changes_MarkFresh() {
	m_bChanged = false;

	const auto fileName = GetCurrentFileName();
	SetWindowTextW(
		m_hWnd,
		std::vformat(
			GetStringResource(IDS_WINDOWTITLE_FONTEDITOR),
			std::make_wformat_args(fileName)).c_str());
}

void App::FontEditorWindow::Changes_MarkDirty() {
	if (m_bChanged)
		return;

	m_bChanged = true;

	const auto fileName = GetCurrentFileName();
	SetWindowTextW(
		m_hWnd,
		std::vformat(
			GetStringResource(IDS_WINDOWTITLE_FONTEDITOR_CHANGED),
			std::make_wformat_args(fileName)).c_str());
}

bool App::FontEditorWindow::Changes_ConfirmIfDirty() {
	if (m_bChanged) {
		switch (MessageBoxW(
			m_hWnd,
			std::wstring(GetStringResource(IDS_CONFIRM_UNSAVEDEXIT)).c_str(),
			GetWindowString(m_hWnd).c_str(),
			MB_YESNOCANCEL)) {
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
		setItemText(ListViewColsSize, std::format(L"{:g}px (!= {:g}px)", element.GetWrappedFont()->font_size(), element.Size));
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
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_ADDNEW)));
			break;
		case xivres::fontgen::codepoint_merge_mode::AddAll:
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_ADDALL)));
			break;
		case xivres::fontgen::codepoint_merge_mode::Replace:
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_REPLACE)));
			break;
		default:
			setItemText(ListViewColsMergeMode, L"???");
			break;
	}
	setItemText(ListViewColsGamma, std::format(L"{:g}", element.Gamma));
	setItemText(ListViewColsRenderer, element.GetRendererRepresentation());
	setItemText(ListViewColsLookup, element.GetLookupRepresentation());
}

std::pair<std::vector<std::shared_ptr<xivres::fontdata::stream>>, std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>>> App::FontEditorWindow::CompileCurrentFontSet(ProgressDialog& progressDialog, Structs::FontSet& fontSet) {
	progressDialog.UpdateStatusMessage(GetStringResource(IDS_EXPORTPROGRESS_LOADFONTS));
	fontSet.ConsolidateFonts();

	{
		progressDialog.UpdateStatusMessage(GetStringResource(IDS_EXPORTPROGRESS_KERNINGPAIRS));
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
			std::wstring s(GetStringResource(IDS_ERROR_KERNINGTABLETOOLARGE));
			for (const auto& s2 : tooManyKernings)
				s += xivres::util::unicode::convert<std::wstring>(s2);
			throw WException(s);
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

		switch (packer.progress_description()) {
			case xivres::fontgen::fontdata_packer::progress_status_t::prepare_source_fonts:
				progressDialog.UpdateStatusMessage(GetStringResource(IDS_COMPILESTATUS_PREPARESOURCEFONTS));
				break;
			case xivres::fontgen::fontdata_packer::progress_status_t::prepare_target_fonts:
				progressDialog.UpdateStatusMessage(GetStringResource(IDS_COMPILESTATUS_PREPARETARGETFONTS));
				break;
			case xivres::fontgen::fontdata_packer::progress_status_t::discover_glyphs:
				progressDialog.UpdateStatusMessage(GetStringResource(IDS_COMPILESTATUS_DISCOVERGLYPHS));
				break;
			case xivres::fontgen::fontdata_packer::progress_status_t::measure_glyphs:
				progressDialog.UpdateStatusMessage(GetStringResource(IDS_COMPILESTATUS_MEASUREGLYPHS));
				break;
			case xivres::fontgen::fontdata_packer::progress_status_t::layout_and_draw:
				progressDialog.UpdateStatusMessage(GetStringResource(IDS_COMPILESTATUS_LAYOUTANDDRAW));
				break;
		}
		progressDialog.UpdateProgress(packer.progress_scaled());
	}
	if (const auto err = packer.get_error_if_failed(); !err.empty())
		throw std::runtime_error(err);

	const auto& fdts = packer.compiled_fontdatas();
	const auto& mips = packer.compiled_mipmap_streams();
	if (mips.empty())
		throw std::runtime_error("DEBUG: No mipmap produced");

	if (fontSet.ExpectedTexCount != static_cast<int>(mips.size())) {
		fontSet.ExpectedTexCount = static_cast<int>(mips.size());
		Changes_MarkDirty();
	}
	return std::make_pair(fdts, mips);
}
