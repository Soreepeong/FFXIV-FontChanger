#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "xivres/textools.h"

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

double App::FontEditorWindow::GetZoom() const noexcept {
	const auto hMonitor = MonitorFromWindow(m_hWnd, MONITOR_DEFAULTTONEAREST);
	UINT newDpiX = 96;
	UINT newDpiY = 96;

	if (FAILED(GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, &newDpiX, &newDpiY))) {
		MONITORINFOEXW mi{};
		mi.cbSize = static_cast<DWORD>(sizeof MONITORINFOEXW);
		GetMonitorInfoW(hMonitor, &mi);
		if (const auto hdc = CreateDCW(L"DISPLAY", mi.szDevice, nullptr, nullptr)) {
			newDpiX = GetDeviceCaps(hdc, LOGPIXELSX);
			newDpiY = GetDeviceCaps(hdc, LOGPIXELSY);
			DeleteDC(hdc);
		}
	}

	return std::min(newDpiY, newDpiX) / 96.;
}
