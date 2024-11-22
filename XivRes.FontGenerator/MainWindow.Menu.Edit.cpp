#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "xivres/textools.h"

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
