#include "pch.h"

#include <commdlg.h>

#include "FontChanger.Presets/FaceFromFont.h"
#include "FontChanger.Presets/Structs.h"
#include "MainWindow.h"
#include "NegativeBearingCodepointsDialog.h"
#include "resource.h"
#include "xivres/textools.h"

LRESULT App::FontEditorWindow::Menu_Edit_Add() {
	if (!m_pActiveFace)
		return 0;

	auto& elements = m_pActiveFace->Elements;
	auto indices = GetSelectedElementIndices();
	if (indices.empty())
		indices.push_back(static_cast<int>(elements.size()));

	// Each new element copies the one before it, except for the characters.
	std::set<const Structs::FaceElement*> added;
	for (const auto pos : indices | std::views::reverse) {
		auto& element = **elements.emplace(elements.begin() + pos, std::make_unique<Structs::FaceElement>());
		if (pos > 0) {
			element = *elements[static_cast<size_t>(pos) - 1];
			element.WrapModifiers.Codepoints.clear();
		}
		added.insert(&element);
	}

	UpdateFaceElementList();
	SelectFaceElements(added);
	OnActiveFaceElementsChanged();

	if (indices.size() == 1)
		ShowEditor(*elements[indices.front()]);

	return 0;
}

namespace FaceFromFont = FontChanger::FaceFromFont;

LRESULT App::FontEditorWindow::Menu_Edit_AddFromFont() {
	if (!m_pActiveFace)
		return 0;

	const auto familyAndSize = FaceFromFont::GetGameFontFamilyAndSize(m_pActiveFace->Name);
	if (!familyAndSize) {
		const auto name = xivres::util::unicode::convert<std::wstring>(m_pActiveFace->Name);
		MessageBoxW(m_hWnd,
			std::vformat(GetStringResource(IDS_ADDFROMFONT_NOTGAMEFONT), std::make_wformat_args(name)).c_str(),
			std::wstring(GetStringResource(IDS_APP)).c_str(),
			MB_OK | MB_ICONWARNING);
		return 0;
	}

	LOGFONTW logFont{};
	CHOOSEFONTW chooseFont{
		.lStructSize = sizeof chooseFont,
		.hwndOwner = m_hWnd,
		.lpLogFont = &logFont,
		.Flags = CF_SCREENFONTS | CF_SCALABLEONLY | CF_NOVERTFONTS | CF_NOSIZESEL | CF_NOSCRIPTSEL | CF_FORCEFONTEXIST,
	};
	if (!ChooseFontW(&chooseFont))
		return 0;

	// The faces of the family in the font set of the active face, each with the elements that draw it with the font.
	std::vector<std::pair<Structs::Face*, std::vector<std::unique_ptr<Structs::FaceElement>>>> additions;
	try {
		const auto lookup = FaceFromFont::GetLookupFromLogFont(logFont);
		for (const auto& pFontSet : m_multiFontSet.FontSets) {
			if (std::ranges::any_of(pFontSet->Faces, [this](const auto& pFace) { return pFace.get() == m_pActiveFace; }))
				additions = FaceFromFont::MakeFamilyElements(*pFontSet, familyAndSize->first, lookup);
		}
	} catch (const std::exception& e) {
		const auto message = xivres::util::unicode::convert<std::wstring>(e.what());
		MessageBoxW(m_hWnd,
			std::vformat(GetStringResource(IDS_ADDFROMFONT_FAILED), std::make_wformat_args(message)).c_str(),
			std::wstring(GetStringResource(IDS_APP)).c_str(),
			MB_OK | MB_ICONERROR);
		return 0;
	}

	for (auto& [pFace, elements] : additions) {
		std::ranges::move(elements, std::back_inserter(pFace->Elements));
		pFace->OnElementChange();
	}

	UpdateFaceElementList();
	Changes_MarkDirty();
	Window_Redraw();
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
	for (const auto i : GetSelectedElementIndices())
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
	if (!m_pActiveFace)
		return 0;

	const auto clipboard = OpenClipboard(m_hWnd);
	if (!clipboard)
		return 0;

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

	auto& elements = m_pActiveFace->Elements;
	auto indices = GetSelectedElementIndices();
	if (indices.empty())
		indices.push_back(static_cast<int>(elements.size()));

	std::set<const Structs::FaceElement*> added;
	for (const auto pos : indices | std::views::reverse) {
		for (const auto& templateElement : parsedTemplateElements | std::views::reverse)
			added.insert(elements.emplace(elements.begin() + pos, std::make_unique<Structs::FaceElement>(templateElement))->get());
	}

	UpdateFaceElementList();
	SelectFaceElements(added);
	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Delete() {
	if (!m_pActiveFace)
		return 0;

	const auto indices = GetSelectedElementIndices();
	if (indices.empty())
		return 0;

	for (const auto index : indices | std::views::reverse)
		m_pActiveFace->Elements.erase(m_pActiveFace->Elements.begin() + index);

	UpdateFaceElementList();
	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_SelectAll() {
	ListView_SetItemState(m_hFaceElementsListView, -1, LVIS_SELECTED, LVIS_SELECTED);
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_Details() {
	for (const auto i : GetSelectedElementIndices())
		ShowEditor(*m_pActiveFace->Elements[i]);
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_ChangeParams(int baselineShift, int horizontalOffset, int letterSpacing, float fontSize) {
	const auto indices = GetSelectedElementIndices();
	if (indices.empty())
		return 0;

	const auto tempDisableRedraw = SuppressRedraw(m_hFaceElementsListView);
	for (const auto i : indices) {
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

	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_ToggleMergeMode() {
	const auto indices = GetSelectedElementIndices();
	if (indices.empty())
		return 0;

	const auto tempDisableRedraw = SuppressRedraw(m_hFaceElementsListView);
	for (const auto i : indices) {
		auto& e = *m_pActiveFace->Elements[i];
		e.MergeMode = static_cast<FontChanger::FixedSizeFont::codepoint_merge_mode>((static_cast<int>(e.MergeMode) + 1) % static_cast<int>(FontChanger::FixedSizeFont::codepoint_merge_mode::Enum_Count_));
		e.OnFontWrappingParametersChange();
		UpdateFaceElementListViewItem(e);
	}

	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_SetVerticalAlignment(FontChanger::FixedSizeFont::vertical_alignment alignment) {
	if (!m_pActiveFace || m_pActiveFace->VerticalAlignment == alignment)
		return 0;

	m_pActiveFace->VerticalAlignment = alignment;
	m_pActiveFace->OnElementChange();
	Changes_MarkDirty();
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_MoveUpOrDown(int direction) {
	auto indices = GetSelectedElementIndices();
	if (indices.empty())
		return 0;

	// Each selected element swaps places with its neighbor, starting from the one nearest to where they move.
	auto& elements = m_pActiveFace->Elements;
	if (direction > 0)
		std::ranges::reverse(indices);

	auto any = false;
	for (const auto index : indices) {
		const auto target = index + direction;
		if (target < 0 || target >= static_cast<int>(elements.size()))
			continue;

		any = true;
		std::swap(elements[index], elements[target]);
	}
	if (!any)
		return 0;

	UpdateFaceElementList();
	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::FaceElementsListView_Clone() {
	if (!m_pActiveFace)
		return 0;

	const auto indices = GetSelectedElementIndices();
	if (indices.empty())
		return 0;

	// The copies go after the last selected element, in their order.
	auto& elements = m_pActiveFace->Elements;
	std::vector<std::unique_ptr<Structs::FaceElement>> clones;
	for (const auto i : indices)
		clones.emplace_back(std::make_unique<Structs::FaceElement>(*elements[i]));

	std::set<const Structs::FaceElement*> added;
	for (const auto& pClone : clones)
		added.insert(pClone.get());
	elements.insert(elements.begin() + indices.back() + 1, std::make_move_iterator(clones.begin()), std::make_move_iterator(clones.end()));

	UpdateFaceElementList();
	SelectFaceElements(added);
	OnActiveFaceElementsChanged();
	return 0;
}

LRESULT App::FontEditorWindow::FaceElementsListView_ShowNegativeBearingCodepoints() {
	if (!m_pActiveFace) return 0;
	const auto index = ListView_GetNextItem(m_hFaceElementsListView, -1, LVNI_SELECTED);
	if (index < 0)
		return 0;

	const auto& element = *m_pActiveFace->Elements[index];
	if (element.Renderer == Structs::RendererEnum::Empty)
		return 0;

	const auto hPrevCursor = SetCursor(LoadCursor(nullptr, IDC_WAIT));
	auto entries = element.GetWrappedFont()->get_negative_lsb_codepoints();
	if (entries.empty()) {
		SetCursor(hPrevCursor);
		MessageBoxW(m_hWnd,
			std::wstring(GetStringResource(IDS_NEGATIVEBEARING_NONE_FOUND)).c_str(),
			std::wstring(GetStringResource(IDS_APP)).c_str(),
			MB_OK | MB_ICONINFORMATION);
		return 0;
	}

	SetCursor(hPrevCursor);
	NegativeBearingCodepointsDialog::Show(m_hWnd, std::move(entries));
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Edit_CreateEmptyCopyFromSelection() {
	if (!m_pActiveFace)
		return 0;

	const auto refIndex = ListView_GetNextItem(m_hFaceElementsListView, -1, LVNI_SELECTED);
	if (refIndex == -1)
		return 0;

	// An empty element of the same metrics, first so that it decides the metrics of the face.
	auto& elements = m_pActiveFace->Elements;
	const auto& ref = *elements[refIndex];
	auto element = std::make_unique<Structs::FaceElement>();
	element->Size = ref.Size;
	element->RendererSpecific = {
		.Empty = {
			.Ascent = static_cast<float>(ref.GetWrappedFont()->ascent()) + ref.WrapModifiers.BaselineShift,
			.LineHeight = static_cast<float>(ref.GetWrappedFont()->line_height()),
		},
	};
	const auto& added = **elements.emplace(elements.begin(), std::move(element));

	UpdateFaceElementList();
	SelectFaceElements({&added});
	OnActiveFaceElementsChanged();
	return 0;
}
