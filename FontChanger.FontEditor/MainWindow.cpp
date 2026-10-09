#include "pch.h"
#include "resource.h"
#include "FontChanger.Presets/Structs.h"
#include "FaceElementEditorDialog.h"
#include "FileHistory.h"
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

void App::FontEditorWindow::OpenFile(std::filesystem::path path) {
	// Relative paths in the file are resolved against its folder.
	path = absolute(path);
	std::ifstream in(path, std::ios::binary);
	if (!in)
		throw std::system_error(std::error_code(static_cast<int>(GetLastError()), std::system_category()));
	auto multiFontSet = nlohmann::json::parse(in).get<Structs::MultiFontSet>();

	FileHistory::Add(path);
	SetCurrentMultiFontSet(std::move(multiFontSet), std::move(path));
}

void App::FontEditorWindow::SetCurrentMultiFontSet(Structs::MultiFontSet multiFontSet, std::filesystem::path path) {
	m_multiFontSet = std::move(multiFontSet);
	m_currentPath = std::move(path);
	UpdateProjectDirectory();

	m_pActiveFace = nullptr;

	UpdateFaceList();
	Changes_MarkFresh();
}

std::wstring App::FontEditorWindow::GetCurrentFileName() const {
	return m_currentPath.empty() ? std::wstring(GetStringResource(IDS_FILENAME_UNTITLED)) : m_currentPath.filename().wstring();
}

App::Structs::FontSet* App::FontEditorWindow::FindFontSet(const Structs::Face& face) const {
	for (const auto& pFontSet : m_multiFontSet.FontSets) {
		if (std::ranges::any_of(pFontSet->Faces, [&face](const auto& pFace) { return pFace.get() == &face; }))
			return pFontSet.get();
	}
	return nullptr;
}

std::vector<int> App::FontEditorWindow::GetSelectedElementIndices() const {
	std::vector<int> indices;
	for (auto i = -1; -1 != (i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED));)
		indices.push_back(i);
	return indices;
}

void App::FontEditorWindow::OnActiveFaceElementsChanged() {
	Changes_MarkDirty();
	m_pActiveFace->OnElementChange();
	Window_Redraw();
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
		}, [this, &element](bool deactivated) {
			if (deactivated)
				m_deactivatedElements.insert(&element);
			else
				m_deactivatedElements.erase(&element);
			Window_Redraw();
		});
	}
}

std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> App::FontEditorWindow::GetPreviewFont() {
	// Elements that are gone may leave their addresses to new ones.
	std::erase_if(m_deactivatedElements, [this](const Structs::FaceElement* p) {
		return std::ranges::none_of(m_multiFontSet.FontSets, [p](const auto& fontSet) {
			return std::ranges::any_of(fontSet->Faces, [p](const auto& face) {
				return std::ranges::any_of(face->Elements, [p](const auto& e) { return e.get() == p; });
			});
		});
	});

	const auto& face = *m_pActiveFace;
	std::set<const Structs::FaceElement*> skip;
	for (const auto& e : face.Elements) {
		if (m_deactivatedElements.contains(e.get()))
			skip.insert(e.get());
	}
	if (skip.empty())
		return face.GetMergedFont();

	// Made again when what it is made of changes: the elements, the fonts and merge modes of those kept, and the alignment.
	std::vector<const void*> key{&face, reinterpret_cast<const void*>(static_cast<size_t>(face.VerticalAlignment))};
	for (const auto& e : face.Elements) {
		key.push_back(e.get());
		if (!skip.contains(e.get())) {
			key.push_back(e->GetWrappedFont().get());
			key.push_back(reinterpret_cast<const void*>(static_cast<size_t>(e->MergeMode)));
		}
	}
	if (key != m_previewFont.Key)
		m_previewFont = {std::move(key), ElementFonts::MergeElements(face, skip)};
	return m_previewFont.Font;
}

void App::FontEditorWindow::UpdateFaceList() {
	const auto tempDisableRedraw = SuppressRedraw(m_hFacesListBox);

	Structs::Face* currentTag = nullptr;
	if (int curSel = ListBox_GetCurSel(m_hFacesListBox); curSel != LB_ERR)
		currentTag = reinterpret_cast<Structs::Face*>(ListBox_GetItemData(m_hFacesListBox, curSel));

	ListBox_ResetContent(m_hFacesListBox);
	auto selectionRestored = false;

	for (auto& pFontSet : m_multiFontSet.FontSets) {
		for (auto& pFace : pFontSet->Faces) {
			const auto index = ListBox_AddString(m_hFacesListBox, xivres::util::unicode::convert<std::wstring>(std::format("{}: {}", pFontSet->TexFilenameFormat, pFace->Name)).c_str());
			ListBox_SetItemData(m_hFacesListBox, index, pFace.get());
			if (currentTag == pFace.get()) {
				ListBox_SetCurSel(m_hFacesListBox, index);
				selectionRestored = true;
			}
		}
	}

	if (!selectionRestored) {
		m_pActiveFace = nullptr;
		if (!m_multiFontSet.FontSets.empty() && !m_multiFontSet.FontSets[0]->Faces.empty()) {
			ListBox_SetCurSel(m_hFacesListBox, 0);
			m_pActiveFace = m_multiFontSet.FontSets[0]->Faces[0].get();
		}
	}

	UpdateFaceElementList();
	Window_Redraw();
}

void App::FontEditorWindow::UpdateFaceElementList() {
	const auto tempDisableRedraw = SuppressRedraw(m_hFaceElementsListView);

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

	// The text is set only when another face became active, to keep the caret where it is otherwise.
	if (const auto previewText = xivres::util::unicode::convert<std::wstring>(m_pActiveFace->PreviewText); previewText != GetWindowString(m_hEdit))
		Edit_SetText(m_hEdit, previewText.c_str());
	Window_Redraw();
}

void App::FontEditorWindow::SelectFaceElements(const std::set<const Structs::FaceElement*>& elements) {
	for (int i = 0, i_ = static_cast<int>(m_pActiveFace->Elements.size()); i < i_; i++)
		ListView_SetItemState(m_hFaceElementsListView, i, elements.contains(m_pActiveFace->Elements[i].get()) ? LVIS_SELECTED : 0, LVIS_SELECTED);
}

std::wstring GetRangeRepresentation(const App::Structs::FaceElement& element) {
	if (element.WrapModifiers.Codepoints.empty())
		return L"(None)";

	std::wstring res;
	std::vector<char32_t> charVec(element.GetBaseFont()->all_codepoints().begin(), element.GetBaseFont()->all_codepoints().end());
	for (const auto& [c1, c2] : element.WrapModifiers.Codepoints) {
		if (!res.empty())
			res += L", ";

		const auto left = std::ranges::lower_bound(charVec, c1);
		const auto right = std::ranges::upper_bound(charVec, c2);
		const auto count = right - left;

		const auto blk = std::lower_bound(xivres::util::unicode::blocks::all_blocks().begin(), xivres::util::unicode::blocks::all_blocks().end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
		if (blk != xivres::util::unicode::blocks::all_blocks().end() && blk->First == c1 && blk->Last == c2) {
			res += std::format(L"{}({})", xivres::util::unicode::convert<std::wstring>(blk->Name), count);
		} else if (c1 == c2) {
			res += std::format(
				L"U+{:04X} [{}]",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			);
		} else {
			res += std::format(
				L"U+{:04X}~{:04X} ({}) {} ~ {}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				count,
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			);
		}
	}

	return res;
}

std::wstring GetRendererRepresentation(const App::Structs::FaceElement& element) {
	using App::Structs::RendererEnum;
	switch (element.Renderer) {
		case RendererEnum::Empty:
			return L"Empty";

		case RendererEnum::PrerenderedGameInstallation:
			return L"Prerendered (Game)";

		case RendererEnum::DirectWrite:
			return std::format(L"DirectWrite ({}, {}, {})",
				element.RendererSpecific.DirectWrite.get_rendering_mode_string(),
				element.RendererSpecific.DirectWrite.get_measuring_mode_string(),
				element.RendererSpecific.DirectWrite.get_grid_fit_mode_string()
			);

		case RendererEnum::FreeType:
			return std::format(L"FreeType ({}, {})", element.RendererSpecific.FreeType.get_render_mode_string(), element.RendererSpecific.FreeType.get_load_flags_string());

		case RendererEnum::GlyphImages:
			return L"SVG/PNG";

		default:
			return L"INVALID";
	}
}

static std::wstring GetLookupRepresentation(const App::Structs::FaceElement& element) {
	switch (element.Renderer) {
		case App::Structs::RendererEnum::DirectWrite:
		case App::Structs::RendererEnum::FreeType:
			return std::format(L"{} ({}, {}, {})",
				xivres::util::unicode::convert<std::wstring>(element.Lookup.Name),
				GetFontWeightName(element.Lookup.Weight),
				GetFontStyleName(element.Lookup.Style),
				GetFontStretchName(element.Lookup.Stretch)
			);

		case App::Structs::RendererEnum::GlyphImages:
			return element.RendererSpecific.GlyphImages.IsEmbedded()
				? std::format(L"(Embedded: {})", element.RendererSpecific.GlyphImages.Embedded.size())
				: xivres::util::unicode::convert<std::wstring>(element.RendererSpecific.GlyphImages.Path);

		default:
			return L"-";
	}
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
		setItemText(ListViewColsSize, std::format(
			L"{} (!= {})",
			FormatPixelValue(element.GetWrappedFont()->font_size()),
			FormatPixelValue(element.Size)));
	} else {
		setItemText(ListViewColsSize, FormatPixelValue(element.GetWrappedFont()->font_size()));
	}
	setItemText(ListViewColsLineHeight, FormatPixelValue(element.GetWrappedFont()->line_height()));
	if (element.WrapModifiers.BaselineShift != 0.f && element.Renderer != Structs::RendererEnum::Empty) {
		setItemText(ListViewColsAscent, std::format(
			L"{}({})",
			FormatPixelValue(element.GetBaseFont()->ascent()),
			FormatPixelValue(element.WrapModifiers.BaselineShift, true)));
	} else {
		setItemText(ListViewColsAscent, FormatPixelValue(element.GetBaseFont()->ascent()));
	}
	setItemText(ListViewColsHorizontalOffset, FormatPixelValue(element.Renderer == Structs::RendererEnum::Empty ? 0.f : element.WrapModifiers.HorizontalOffset));
	setItemText(ListViewColsLetterSpacing, FormatPixelValue(element.Renderer == Structs::RendererEnum::Empty ? 0.f : element.WrapModifiers.LetterSpacing));
	setItemText(ListViewColsCodepoints, GetRangeRepresentation(element));
	setItemText(ListViewColsGlyphCount, std::format(L"{}", element.GetWrappedFont()->all_codepoints().size()));
	switch (element.MergeMode) {
		case FontChanger::FixedSizeFont::codepoint_merge_mode::AddNew:
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_ADDNEW)));
			break;
		case FontChanger::FixedSizeFont::codepoint_merge_mode::AddAll:
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_ADDALL)));
			break;
		case FontChanger::FixedSizeFont::codepoint_merge_mode::Replace:
			setItemText(ListViewColsMergeMode, std::wstring(GetStringResource(IDS_CODEPOINTMERGEMODE_REPLACE)));
			break;
		default:
			setItemText(ListViewColsMergeMode, L"???");
			break;
	}
	setItemText(ListViewColsGamma, std::format(L"{:g}", element.Gamma));
	setItemText(ListViewColsRenderer, GetRendererRepresentation(element));
	setItemText(ListViewColsLookup, GetLookupRepresentation(element));
}
