#include "pch.h"
#include "FaceElementEditorDialog.h"
#include "FontChanger.Presets/FaceFromFont.h"
#include "resource.h"
#include "FontChanger.Presets/Structs.h"
#include "MainWindow.h"
#include "xivres/textools.h"

using FontChanger::FaceFromFont::GetGameFontFamilyAndSize;

namespace {
	// Returns the size that the face is drawn at: that of its first element, which the merged font takes its size from,
	// or if it has no elements, the size of the game font by its name.
	std::optional<float> GetFaceSize(const App::Structs::Face& face) {
		if (!face.Elements.empty())
			return face.Elements.front()->Size;
		if (const auto familyAndSize = GetGameFontFamilyAndSize(face.Name))
			return familyAndSize->second;
		return std::nullopt;
	}

	// Asks for a size in pixels, starting with size; returns whether a positive size was entered.
	bool AskForSize(HWND hParent, float& size) {
		const auto hglob = LoadResourceWithLanguageFallback(RT_DIALOG, IDD_SCALEFACE);
		return IDOK == DialogBoxIndirectParamW(
			g_hInstance,
			static_cast<DLGTEMPLATE*>(LockResource(hglob.get())),
			hParent,
			[](HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) -> INT_PTR {
				switch (message) {
					case WM_INITDIALOG: {
						SetWindowLongPtrW(hwnd, DWLP_USER, lParam);
						const auto edit = GetDlgItem(hwnd, IDC_EDIT_SCALEFACE_SIZE);
						SetWindowTextW(edit, std::format(L"{:g}", *reinterpret_cast<float*>(lParam)).c_str());
						SendMessageW(hwnd, WM_NEXTDLGCTL, reinterpret_cast<WPARAM>(edit), TRUE);
						return FALSE;
					}
					case WM_COMMAND:
						switch (LOWORD(wParam)) {
							case IDOK: {
								const auto text = GetWindowString(GetDlgItem(hwnd, IDC_EDIT_SCALEFACE_SIZE));
								wchar_t* end = nullptr;
								const auto value = std::wcstof(text.c_str(), &end);
								if (end == text.c_str() || *end || !std::isfinite(value) || value <= 0.f) {
									MessageBeep(MB_ICONWARNING);
									SendMessageW(hwnd, WM_NEXTDLGCTL, reinterpret_cast<WPARAM>(GetDlgItem(hwnd, IDC_EDIT_SCALEFACE_SIZE)), TRUE);
									return TRUE;
								}
								*reinterpret_cast<float*>(GetWindowLongPtrW(hwnd, DWLP_USER)) = value;
								EndDialog(hwnd, IDOK);
								return TRUE;
							}
							case IDCANCEL:
								EndDialog(hwnd, IDCANCEL);
								return TRUE;
						}
						break;
				}
				return FALSE;
			},
			reinterpret_cast<LPARAM>(&size));
	}
}

void App::FontEditorWindow::CloseEditors(const Structs::Face& face) {
	for (const auto& pElement : face.Elements) {
		if (const auto it = m_editors.find(pElement.get()); it != m_editors.end()) {
			if (it->second)
				DestroyWindow(it->second->m_hWnd);
			m_editors.erase(it);
		}
	}
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
			if (const auto index = ListBox_GetCurSel(m_hFacesListBox); index != LB_ERR) {
				m_pActiveFace = reinterpret_cast<Structs::Face*>(ListBox_GetItemData(m_hFacesListBox, index));
				m_nPreviewScrollY = 0;
				UpdateFaceElementList();
			}
			return 0;
		}
	}
	return 0;
}

LRESULT App::FontEditorWindow::FaceListBox_OnContextMenu(POINT screenPos) {
	int index;
	if (screenPos.x == -1 && screenPos.y == -1) {
		index = ListBox_GetCurSel(m_hFacesListBox);
		if (index == LB_ERR)
			return 0;

		RECT rc;
		ListBox_GetItemRect(m_hFacesListBox, index, &rc);
		screenPos = {rc.left, rc.bottom};
		ClientToScreen(m_hFacesListBox, &screenPos);
	} else {
		POINT pt = screenPos;
		ScreenToClient(m_hFacesListBox, &pt);
		const auto hit = static_cast<DWORD>(SendMessageW(m_hFacesListBox, LB_ITEMFROMPOINT, 0, MAKELPARAM(pt.x, pt.y)));
		if (HIWORD(hit))
			return 0;

		// The menu is about the clicked font, so show it as the current one.
		index = LOWORD(hit);
		if (index != ListBox_GetCurSel(m_hFacesListBox)) {
			ListBox_SetCurSel(m_hFacesListBox, index);
			FaceListBox_OnCommand(LBN_SELCHANGE);
		}
	}

	const auto pFace = reinterpret_cast<Structs::Face*>(ListBox_GetItemData(m_hFacesListBox, index));
	const auto pFontSet = pFace ? FindFontSet(*pFace) : nullptr;
	if (!pFontSet)
		return 0;

	const auto familyAndSize = GetGameFontFamilyAndSize(pFace->Name);
	auto hasOtherSizes = false;
	if (familyAndSize) {
		for (const auto& pOther : pFontSet->Faces) {
			if (const auto other = GetGameFontFamilyAndSize(pOther->Name); pOther.get() != pFace && other && other->first == familyAndSize->first)
				hasOtherSizes = true;
		}
	}

	const auto faceName = xivres::util::unicode::convert<std::wstring>(pFace->Name);
	const auto familyName = familyAndSize ? xivres::util::unicode::convert<std::wstring>(familyAndSize->first) : faceName;
	const auto replaceText = std::vformat(GetStringResource(IDS_FACELIST_REPLACEOTHERSIZES), std::make_wformat_args(familyName, faceName));

	enum : UINT {
		IdScaleToSize = 1,
		IdReplaceOtherSizes,
	};
	const auto hMenu = CreatePopupMenu();
	AppendMenuW(hMenu, MF_STRING | (pFace->Elements.empty() ? MF_GRAYED : 0), IdScaleToSize, std::wstring(GetStringResource(IDS_FACELIST_SCALETOSIZE)).c_str());
	AppendMenuW(hMenu, MF_STRING | (hasOtherSizes ? 0 : MF_GRAYED), IdReplaceOtherSizes, replaceText.c_str());
	const auto command = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, screenPos.x, screenPos.y, m_hWnd, nullptr);
	DestroyMenu(hMenu);

	switch (command) {
		case IdScaleToSize:
			FaceListBox_ScaleToSize(*pFace);
			break;
		case IdReplaceOtherSizes:
			FaceListBox_ReplaceOtherSizes(*pFontSet, *pFace);
			break;
	}
	return 0;
}

void App::FontEditorWindow::FaceListBox_ScaleToSize(Structs::Face& face) {
	const auto currentSize = GetFaceSize(face);
	if (face.Elements.empty() || !currentSize || *currentSize <= 0.f)
		return;

	auto size = *currentSize;
	if (!AskForSize(m_hWnd, size) || size == *currentSize)
		return;

	// Editors would show, and revert to, the values from before the scaling.
	CloseEditors(face);

	for (const auto& pElement : face.Elements) {
		pElement->Scale(size / *currentSize);
		UpdateFaceElementListViewItem(*pElement);
	}
	face.OnElementChange();
	Changes_MarkDirty();
	Window_Redraw();
}

void App::FontEditorWindow::FaceListBox_ReplaceOtherSizes(Structs::FontSet& fontSet, const Structs::Face& face) {
	const auto familyAndSize = GetGameFontFamilyAndSize(face.Name);
	const auto faceSize = GetFaceSize(face);
	if (!familyAndSize || !faceSize || *faceSize <= 0.f)
		return;

	std::vector<std::pair<Structs::Face*, float>> targets;
	std::wstring targetNames;
	for (const auto& pOther : fontSet.Faces) {
		const auto other = GetGameFontFamilyAndSize(pOther->Name);
		if (pOther.get() == &face || !other || other->first != familyAndSize->first)
			continue;

		const auto size = GetFaceSize(*pOther).value_or(other->second);
		targets.emplace_back(pOther.get(), size);
		if (!targetNames.empty())
			targetNames += L'\n';
		targetNames += std::format(L"{} ({})", xivres::util::unicode::convert<std::wstring>(pOther->Name), FormatPixelValue(size));
	}
	if (targets.empty())
		return;

	const auto faceName = xivres::util::unicode::convert<std::wstring>(face.Name);
	if (MessageBoxW(
		m_hWnd,
		std::vformat(GetStringResource(IDS_FACELIST_CONFIRMREPLACEOTHERSIZES), std::make_wformat_args(faceName, targetNames)).c_str(),
		GetWindowString(m_hWnd).c_str(),
		MB_YESNO | MB_ICONQUESTION) != IDYES)
		return;

	for (const auto& [pTarget, size] : targets) {
		// Editors of the replaced elements would be left with elements that no longer exist.
		CloseEditors(*pTarget);

		pTarget->Elements.clear();
		for (const auto& pElement : face.Elements)
			pTarget->Elements.emplace_back(std::make_unique<Structs::FaceElement>(*pElement))->Scale(size / *faceSize);
		pTarget->VerticalAlignment = face.VerticalAlignment;
		pTarget->OnElementChange();
	}

	Changes_MarkDirty();
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
	const auto tempDisableRedraw = SuppressRedraw(m_hFaceElementsListView);

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

	// The selected elements move, in their order, to end at the dropped item.
	auto& elements = m_pActiveFace->Elements;
	const auto sourceIndices = GetSelectedElementIndices();
	std::vector<const Structs::FaceElement*> previousOrder;
	for (const auto& pElement : elements)
		previousOrder.push_back(pElement.get());

	std::vector<std::unique_ptr<Structs::FaceElement>> moved, others;
	for (int i = 0, i_ = static_cast<int>(elements.size()); i < i_; i++)
		(std::ranges::binary_search(sourceIndices, i) ? moved : others).emplace_back(std::move(elements[i]));

	const auto insertAt = (std::min)(others.size(), static_cast<size_t>((std::max<int>)(0, 1 + lvhti.iItem - static_cast<int>(moved.size()))));
	std::vector<std::unique_ptr<Structs::FaceElement>> reordered;
	std::ranges::move(others.begin(), others.begin() + insertAt, std::back_inserter(reordered));
	std::ranges::move(moved, std::back_inserter(reordered));
	std::ranges::move(others.begin() + insertAt, others.end(), std::back_inserter(reordered));

	elements = std::move(reordered);
	if (std::ranges::equal(elements, previousOrder, {}, &std::unique_ptr<Structs::FaceElement>::get))
		return false;

	UpdateFaceElementList();
	OnActiveFaceElementsChanged();
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

LRESULT App::FontEditorWindow::FaceElementsListView_OnRightClick(NMITEMACTIVATE& nmia) {
	if (!m_pActiveFace || nmia.iItem < 0 || !m_hFaceElementContextMenu)
		return 0;
	POINT pt{nmia.ptAction};
	ClientToScreen(m_hFaceElementsListView, &pt);
	TrackPopupMenuEx(GetSubMenu(m_hFaceElementContextMenu, 0), TPM_RIGHTBUTTON, pt.x, pt.y, m_hWnd, nullptr);
	return 0;
}

double App::FontEditorWindow::GetZoom() const noexcept {
	return GetZoomFromWindow(m_hWnd);
}
