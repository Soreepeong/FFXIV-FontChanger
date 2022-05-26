#pragma once

#include "Structs.h"

namespace App {
	class FaceElementEditorDialog {
		App::Structs::FaceElement& m_element;
		App::Structs::FaceElement m_elementOriginal;
		HWND m_hParentWnd;
		bool m_bOpened = false;
		std::function<void()> m_onFontChanged;
		bool m_bBaseFontChanged = false;
		bool m_bWrappedFontChanged = false;

		enum : UINT {
			WmApp = WM_APP,
			WmFontSizeTextChanged,
		};

		struct ControlStruct {
			HWND Window;
			HWND OkButton = GetDlgItem(Window, IDOK);
			HWND CancelButton = GetDlgItem(Window, IDCANCEL);
			HWND FontRendererCombo = GetDlgItem(Window, IDC_COMBO_FONT_RENDERER);
			HWND FontCombo = GetDlgItem(Window, IDC_COMBO_FONT);
			HWND FontSizeCombo = GetDlgItem(Window, IDC_COMBO_FONT_SIZE);
			HWND FontWeightCombo = GetDlgItem(Window, IDC_COMBO_FONT_WEIGHT);
			HWND FontStyleCombo = GetDlgItem(Window, IDC_COMBO_FONT_STYLE);
			HWND FontStretchCombo = GetDlgItem(Window, IDC_COMBO_FONT_STRETCH);
			HWND EmptyAscentEdit = GetDlgItem(Window, IDC_EDIT_EMPTY_ASCENT);
			HWND EmptyLineHeightEdit = GetDlgItem(Window, IDC_EDIT_EMPTY_LINEHEIGHT);
			HWND FreeTypeNoHintingCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOHINTING);
			HWND FreeTypeNoBitmapCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOBITMAP);
			HWND FreeTypeForceAutohintCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_FORCEAUTOHINT);
			HWND FreeTypeNoAutohintCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOAUTOHINT);
			HWND DirectWriteRenderModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_RENDERMODE);
			HWND DirectWriteMeasureModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_MEASUREMODE);
			HWND DirectWriteGridFitModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_GRIDFITMODE);
			HWND AdjustmentBaselineShiftEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_BASELINESHIFT);
			HWND AdjustmentLetterSpacingEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_LETTERSPACING);
			HWND AdjustmentHorizontalOffsetEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_HORIZONTALOFFSET);
			HWND AdjustmentGammaEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_GAMMA);
			HWND CodepointsList = GetDlgItem(Window, IDC_LIST_CODEPOINTS);
			HWND CodepointsDeleteButton = GetDlgItem(Window, IDC_BUTTON_CODEPOINTS_DELETE);
			HWND CodepointsOverwriteCheck = GetDlgItem(Window, IDC_CHECK_CODEPOINTS_OVERWRITE);
			HWND UnicodeBlockSearchNameEdit = GetDlgItem(Window, IDC_EDIT_UNICODEBLOCKS_SEARCH);
			HWND UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput = GetDlgItem(Window, IDC_CHECK_UNICODEBLOCKS_SHOWBLOCKSWITHANYOFCHARACTERSINPUT);
			HWND UnicodeBlockSearchResultList = GetDlgItem(Window, IDC_LIST_UNICODEBLOCKS_SEARCHRESULTS);
			HWND UnicodeBlockSearchSelectedPreviewEdit = GetDlgItem(Window, IDC_EDIT_UNICODEBLOCKS_RANGEPREVIEW);
			HWND UnicodeBlockSearchAddAll = GetDlgItem(Window, IDC_BUTTON_UNICODEBLOCKS_ADDALL);
			HWND UnicodeBlockSearchAdd = GetDlgItem(Window, IDC_BUTTON_UNICODEBLOCKS_ADD);
			HWND CustomRangeEdit = GetDlgItem(Window, IDC_EDIT_ADDCUSTOMRANGE_INPUT);
			HWND CustomRangePreview = GetDlgItem(Window, IDC_EDIT_ADDCUSTOMRANGE_PREVIEW);
			HWND CustomRangeAdd = GetDlgItem(Window, IDC_BUTTON_ADDCUSTOMRANGE_ADD);
		} *m_controls = nullptr;

	public:
		FaceElementEditorDialog(HWND hParentWnd, App::Structs::FaceElement& element, std::function<void()> onFontChanged)
			: m_hParentWnd(hParentWnd)
			, m_element(element)
			, m_elementOriginal(element)
			, m_onFontChanged(onFontChanged) {

			std::unique_ptr<std::remove_pointer<HGLOBAL>::type, decltype(FreeResource)*> hglob(LoadResource(g_hInstance, FindResourceW(g_hInstance, MAKEINTRESOURCE(IDD_FACEELEMENTEDITOR), RT_DIALOG)), FreeResource);
			CreateDialogIndirectParamW(
				g_hInstance,
				reinterpret_cast<DLGTEMPLATE*>(LockResource(hglob.get())),
				m_hParentWnd,
				DlgProcStatic,
				reinterpret_cast<LPARAM>(this));
		}

		~FaceElementEditorDialog() {
			delete m_controls;
		}

		bool IsOpened() const {
			return m_bOpened;
		}

		void Activate() const {
			BringWindowToTop(m_controls->Window);
		}

		bool ConsumeDialogMessage(MSG& msg) {
			return m_controls && IsDialogMessage(m_controls->Window, &msg);
		}

	private:
		INT_PTR OkButton_OnCommand(uint16_t notiCode) {
			EndDialog(m_controls->Window, 0);
			m_bOpened = false;
			return 0;
		}

		INT_PTR CancelButton_OnCommand(uint16_t notiCode) {
			m_element = std::move(m_elementOriginal);
			EndDialog(m_controls->Window, 0);
			m_bOpened = false;

			if (m_bBaseFontChanged)
				OnBaseFontChanged();
			else if (m_bWrappedFontChanged)
				OnWrappedFontChanged();
			return 0;
		}

		INT_PTR FontRendererCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			m_element.Renderer = static_cast<App::Structs::RendererEnum>(ComboBox_GetCurSel(m_controls->FontRendererCombo));
			SetControlsEnabledOrDisabled();
			RepopulateFontCombobox();
			OnBaseFontChanged();
			RefreshUnicodeBlockSearchResults();
			return 0;
		}

		INT_PTR FontCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			RepopulateFontSubComboBox();
			m_element.Lookup.Name = XivRes::Unicode::Convert<std::string>(GetWindowString(m_controls->FontCombo));
			OnBaseFontChanged();
			RefreshUnicodeBlockSearchResults();
			return 0;
		}

		INT_PTR FontSizeCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE && notiCode != CBN_EDITUPDATE)
				return 0;

			PostMessageW(m_controls->Window, WmFontSizeTextChanged, 0, 0);
			return 0;
		}

		INT_PTR FontWeightCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			if (const auto w = static_cast<DWRITE_FONT_WEIGHT>(GetWindowInt(m_controls->FontWeightCombo)); w != m_element.Lookup.Weight) {
				m_element.Lookup.Weight = w;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR FontStyleCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			DWRITE_FONT_STYLE newStyle;
			if (const auto prevText = GetWindowString(m_controls->FontStyleCombo); prevText == L"Normal")
				newStyle = DWRITE_FONT_STYLE_NORMAL;
			else if (prevText == L"Oblique")
				newStyle = DWRITE_FONT_STYLE_OBLIQUE;
			else if (prevText == L"Italic")
				newStyle = DWRITE_FONT_STYLE_ITALIC;
			else
				newStyle = DWRITE_FONT_STYLE_NORMAL;
			if (newStyle != m_element.Lookup.Style) {
				m_element.Lookup.Style = newStyle;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR FontStretchCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			DWRITE_FONT_STRETCH newStretch;
			if (const auto prevText = GetWindowString(m_controls->FontStretchCombo); prevText == L"Ultra Condensed")
				newStretch = DWRITE_FONT_STRETCH_ULTRA_CONDENSED;
			else if (prevText == L"Extra Condensed")
				newStretch = DWRITE_FONT_STRETCH_EXTRA_CONDENSED;
			else if (prevText == L"Condensed")
				newStretch = DWRITE_FONT_STRETCH_CONDENSED;
			else if (prevText == L"Semi Condensed")
				newStretch = DWRITE_FONT_STRETCH_SEMI_CONDENSED;
			else if (prevText == L"Normal")
				newStretch = DWRITE_FONT_STRETCH_NORMAL;
			else if (prevText == L"Medium")
				newStretch = DWRITE_FONT_STRETCH_MEDIUM;
			else if (prevText == L"Semi Expanded")
				newStretch = DWRITE_FONT_STRETCH_SEMI_EXPANDED;
			else if (prevText == L"Expanded")
				newStretch = DWRITE_FONT_STRETCH_EXPANDED;
			else if (prevText == L"Extra Expanded")
				newStretch = DWRITE_FONT_STRETCH_EXTRA_EXPANDED;
			else if (prevText == L"Ultra Expanded")
				newStretch = DWRITE_FONT_STRETCH_ULTRA_EXPANDED;
			else
				newStretch = DWRITE_FONT_STRETCH_NORMAL;
			if (newStretch != newStretch) {
				newStretch = newStretch;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR EmptyAscentEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowInt(m_controls->EmptyAscentEdit); n != m_element.RendererSpecific.Empty.Ascent) {
				m_element.RendererSpecific.Empty.Ascent = n;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR EmptyLineHeightEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowInt(m_controls->EmptyLineHeightEdit); n != m_element.RendererSpecific.Empty.LineHeight) {
				m_element.RendererSpecific.Empty.LineHeight = n;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR FreeTypeCheck_OnClick(uint16_t notiCode, uint16_t id, HWND hWnd) {
			if (notiCode != BN_CLICKED)
				return 0;

			const auto newFlags = 0
				| (Button_GetCheck(m_controls->FreeTypeNoHintingCheck) ? FT_LOAD_NO_HINTING : 0)
				| (Button_GetCheck(m_controls->FreeTypeNoBitmapCheck) ? FT_LOAD_NO_BITMAP : 0)
				| (Button_GetCheck(m_controls->FreeTypeForceAutohintCheck) ? FT_LOAD_FORCE_AUTOHINT : 0)
				| (Button_GetCheck(m_controls->FreeTypeNoAutohintCheck) ? FT_LOAD_NO_AUTOHINT : 0);

			if (newFlags != m_element.RendererSpecific.FreeType.LoadFlags) {
				m_element.RendererSpecific.FreeType.LoadFlags = newFlags;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR DirectWriteRenderModeCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			if (const auto v = static_cast<DWRITE_RENDERING_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteRenderModeCombo));
				v != m_element.RendererSpecific.DirectWrite.RenderMode) {
				m_element.RendererSpecific.DirectWrite.RenderMode = v;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR DirectWriteMeasureModeCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			if (const auto v = static_cast<DWRITE_MEASURING_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteMeasureModeCombo));
				v != m_element.RendererSpecific.DirectWrite.MeasureMode) {
				m_element.RendererSpecific.DirectWrite.MeasureMode = v;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR DirectWriteGridFitModeCombo_OnCommand(uint16_t notiCode) {
			if (notiCode != CBN_SELCHANGE)
				return 0;

			if (const auto v = static_cast<DWRITE_GRID_FIT_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteGridFitModeCombo));
				v != m_element.RendererSpecific.DirectWrite.GridFitMode) {
				m_element.RendererSpecific.DirectWrite.GridFitMode = v;
				OnBaseFontChanged();
			}
			return 0;
		}

		INT_PTR AdjustmentBaselineShiftEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowInt(m_controls->AdjustmentBaselineShiftEdit); n != m_element.WrapModifiers.BaselineShift) {
				m_element.WrapModifiers.BaselineShift = n;
				OnWrappedFontChanged();
			}
			return 0;
		}

		INT_PTR AdjustmentLetterSpacingEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowInt(m_controls->AdjustmentLetterSpacingEdit); n != m_element.WrapModifiers.LetterSpacing) {
				m_element.WrapModifiers.LetterSpacing = n;
				OnWrappedFontChanged();
			}
			return 0;
		}

		INT_PTR AdjustmentHorizontalOffsetEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowInt(m_controls->AdjustmentHorizontalOffsetEdit); n != m_element.WrapModifiers.HorizontalOffset) {
				m_element.WrapModifiers.HorizontalOffset = n;
				OnWrappedFontChanged();
			}
			return 0;
		}

		INT_PTR AdjustmentGammaEdit_OnChange(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			if (const auto n = GetWindowFloat(m_controls->AdjustmentGammaEdit); n != m_element.Gamma) {
				m_element.Gamma = n;
				OnWrappedFontChanged();
			}
			return 0;
		}

		INT_PTR CodepointsList_OnCommand(uint16_t notiCode) {
			if (notiCode == LBN_DBLCLK)
				return CodepointsDeleteButton_OnCommand(BN_CLICKED);

			return 0;
		}

		INT_PTR CodepointsDeleteButton_OnCommand(uint16_t notiCode) {
			std::vector<int> selItems(ListBox_GetSelCount(m_controls->CodepointsList));
			if (selItems.empty())
				return 0;

			ListBox_GetSelItems(m_controls->CodepointsList, static_cast<int>(selItems.size()), &selItems[0]);
			std::ranges::sort(selItems, std::greater<>());
			for (const auto itemIndex : selItems) {
				m_element.WrapModifiers.Codepoints.erase(m_element.WrapModifiers.Codepoints.begin() + itemIndex);
				ListBox_DeleteString(m_controls->CodepointsList, itemIndex);
			}

			OnWrappedFontChanged();
			return 0;
		}

		INT_PTR CodepointsOverwriteCheck_OnCommand(uint16_t notiCode) {
			m_element.Overwrite = Button_GetCheck(m_controls->CodepointsOverwriteCheck);
			OnWrappedFontChanged();
			return 0;
		}

		INT_PTR UnicodeBlockSearchNameEdit_OnCommand(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			RefreshUnicodeBlockSearchResults();
			return 0;
		}

		INT_PTR UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(uint16_t notiCode) {
			RefreshUnicodeBlockSearchResults();
			return 0;
		}

		INT_PTR UnicodeBlockSearchResultList_OnCommand(uint16_t notiCode) {
			if (notiCode == LBN_SELCHANGE || notiCode == LBN_SELCANCEL) {
				std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
				if (selItems.empty())
					return 0;

				ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), &selItems[0]);

				std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());
				std::wstring containingChars;

				containingChars.reserve(8192);

				for (const auto itemIndex : selItems) {
					const auto& block = *reinterpret_cast<XivRes::Unicode::UnicodeBlocks::BlockDefinition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));

					for (auto it = std::ranges::lower_bound(charVec, block.First), it_ = std::ranges::upper_bound(charVec, block.Last); it != it_; ++it) {
						XivRes::Unicode::RepresentChar(containingChars, *it);
						if (containingChars.size() >= 8192)
							break;
					}

					if (containingChars.size() >= 8192)
						break;
				}

				Static_SetText(m_controls->UnicodeBlockSearchSelectedPreviewEdit, containingChars.c_str());
			} else if (notiCode == LBN_DBLCLK) {
				return UnicodeBlockSearchAdd_OnCommand(BN_CLICKED);
			}
			return 0;
		}

		INT_PTR UnicodeBlockSearchAddAll_OnCommand(uint16_t notiCode) {
			auto changed = false;
			std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());
			for (int i = 0, i_ = ListBox_GetCount(m_controls->UnicodeBlockSearchResultList); i < i_; i++) {
				const auto& block = *reinterpret_cast<const XivRes::Unicode::UnicodeBlocks::BlockDefinition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, i));
				changed |= AddNewCodepointRange(block.First, block.Last, charVec);
			}

			if (changed)
				OnWrappedFontChanged();

			return 0;
		}

		INT_PTR UnicodeBlockSearchAdd_OnCommand(uint16_t notiCode) {
			std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
			if (selItems.empty())
				return 0;

			ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), &selItems[0]);

			auto changed = false;
			std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());
			for (const auto itemIndex : selItems) {
				const auto& block = *reinterpret_cast<const XivRes::Unicode::UnicodeBlocks::BlockDefinition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));
				changed |= AddNewCodepointRange(block.First, block.Last, charVec);
			}

			if (changed)
				OnWrappedFontChanged();

			return 0;
		}

		INT_PTR CustomRangeEdit_OnCommand(uint16_t notiCode) {
			if (notiCode != EN_CHANGE)
				return 0;

			std::wstring description;
			for (const auto& [c1, c2] : ParseCustomRangeString()) {
				if (!description.empty())
					description += L", ";
				if (c1 == c2) {
					description += std::format(
						L"U+{:04X} {}",
						static_cast<uint32_t>(c1),
						XivRes::Unicode::RepresentChar<std::wstring>(c1)
					);
				} else {
					description += std::format(
						L"U+{:04X}~{:04X} {}~{}",
						static_cast<uint32_t>(c1),
						static_cast<uint32_t>(c2),
						XivRes::Unicode::RepresentChar<std::wstring>(c1),
						XivRes::Unicode::RepresentChar<std::wstring>(c2)
					);
				}
			}

			Edit_SetText(m_controls->CustomRangePreview, &description[0]);

			return 0;
		}

		INT_PTR CustomRangeAdd_OnCommand(uint16_t notiCode) {
			std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());

			auto changed = false;
			for (const auto& [c1, c2] : ParseCustomRangeString())
				changed |= AddNewCodepointRange(c1, c2, charVec);

			if (changed)
				OnWrappedFontChanged();

			return 0;
		}

		INT_PTR Dialog_OnInitDialog() {
			m_bOpened = true;
			SetControlsEnabledOrDisabled();
			RepopulateFontCombobox();
			RefreshUnicodeBlockSearchResults();
			ComboBox_AddString(m_controls->FontRendererCombo, L"Empty");
			ComboBox_AddString(m_controls->FontRendererCombo, L"Prerendered (Game)");
			ComboBox_AddString(m_controls->FontRendererCombo, L"DirectWrite");
			ComboBox_AddString(m_controls->FontRendererCombo, L"FreeType");
			ComboBox_SetCurSel(m_controls->FontRendererCombo, static_cast<int>(m_element.Renderer));
			ComboBox_SetText(m_controls->FontCombo, XivRes::Unicode::Convert<std::wstring>(m_element.Lookup.Name).c_str());
			Edit_SetText(m_controls->EmptyAscentEdit, std::format(L"{}", m_element.RendererSpecific.Empty.Ascent).c_str());
			Edit_SetText(m_controls->EmptyLineHeightEdit, std::format(L"{}", m_element.RendererSpecific.Empty.LineHeight).c_str());
			Button_SetCheck(m_controls->FreeTypeNoHintingCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_HINTING) ? TRUE : FALSE);
			Button_SetCheck(m_controls->FreeTypeNoBitmapCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_BITMAP) ? TRUE : FALSE);
			Button_SetCheck(m_controls->FreeTypeForceAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_FORCE_AUTOHINT) ? TRUE : FALSE);
			Button_SetCheck(m_controls->FreeTypeNoAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_AUTOHINT) ? TRUE : FALSE);
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"Default");
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"Aliased");
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"GDI Classic");
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"GDI Natural");
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"Natural");
			ComboBox_AddString(m_controls->DirectWriteRenderModeCombo, L"Natural Symmetric");
			ComboBox_SetCurSel(m_controls->DirectWriteRenderModeCombo, static_cast<int>(m_element.RendererSpecific.DirectWrite.RenderMode));
			ComboBox_AddString(m_controls->DirectWriteMeasureModeCombo, L"Natural");
			ComboBox_AddString(m_controls->DirectWriteMeasureModeCombo, L"GDI Classic");
			ComboBox_AddString(m_controls->DirectWriteMeasureModeCombo, L"GDI Natural");
			ComboBox_SetCurSel(m_controls->DirectWriteMeasureModeCombo, static_cast<int>(m_element.RendererSpecific.DirectWrite.MeasureMode));
			ComboBox_AddString(m_controls->DirectWriteGridFitModeCombo, L"Default");
			ComboBox_AddString(m_controls->DirectWriteGridFitModeCombo, L"Disabled");
			ComboBox_AddString(m_controls->DirectWriteGridFitModeCombo, L"Enabled");
			ComboBox_SetCurSel(m_controls->DirectWriteGridFitModeCombo, static_cast<int>(m_element.RendererSpecific.DirectWrite.GridFitMode));

			Edit_SetText(m_controls->AdjustmentBaselineShiftEdit, std::format(L"{}", m_element.WrapModifiers.BaselineShift).c_str());
			Edit_SetText(m_controls->AdjustmentLetterSpacingEdit, std::format(L"{}", m_element.WrapModifiers.LetterSpacing).c_str());
			Edit_SetText(m_controls->AdjustmentHorizontalOffsetEdit, std::format(L"{}", m_element.WrapModifiers.HorizontalOffset).c_str());
			Edit_SetText(m_controls->AdjustmentGammaEdit, std::format(L"{:g}", m_element.Gamma).c_str());

			for (const auto& controlHwnd : {
				m_controls->EmptyAscentEdit,
				m_controls->EmptyLineHeightEdit,
				m_controls->AdjustmentBaselineShiftEdit,
				m_controls->AdjustmentLetterSpacingEdit,
				m_controls->AdjustmentHorizontalOffsetEdit
				}) {
				SetWindowSubclass(controlHwnd, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
					if (msg == WM_KEYDOWN && wParam == VK_DOWN)
						SetWindowInt(hWnd, (std::max)(-128, GetWindowInt(hWnd) + 1));
					else if (msg == WM_KEYDOWN && wParam == VK_UP)
						SetWindowInt(hWnd, (std::min)(127, GetWindowInt(hWnd) - 1));
					else
						return DefSubclassProc(hWnd, msg, wParam, lParam);
					return 0;
				}, 1, 0);
			}
			SetWindowSubclass(m_controls->AdjustmentGammaEdit, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
				if (msg == WM_KEYDOWN && wParam == VK_DOWN)
					SetWindowFloat(hWnd, (std::max)(0.1f, GetWindowFloat(hWnd) + 0.1f));
				else if (msg == WM_KEYDOWN && wParam == VK_UP)
					SetWindowFloat(hWnd, (std::min)(3.0f, GetWindowFloat(hWnd) - 0.1f));
				else
					return DefSubclassProc(hWnd, msg, wParam, lParam);
				return 0;
			}, 1, 0);

			std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());
			for (int i = 0, i_ = static_cast<int>(m_element.WrapModifiers.Codepoints.size()); i < i_; i++)
				AddCodepointRangeToListBox(i, m_element.WrapModifiers.Codepoints[i].first, m_element.WrapModifiers.Codepoints[i].second, charVec);

			Button_SetCheck(m_controls->CodepointsOverwriteCheck, m_element.Overwrite ? TRUE : FALSE);

			RECT rc, rcParent;
			GetWindowRect(m_controls->Window, &rc);
			GetWindowRect(m_hParentWnd, &rcParent);
			rc.right -= rc.left;
			rc.bottom -= rc.top;
			rc.left = (rcParent.left + rcParent.right - rc.right) / 2;
			rc.top = (rcParent.top + rcParent.bottom - rc.bottom) / 2;
			rc.right += rc.left;
			rc.bottom += rc.top;
			SetWindowPos(m_controls->Window, nullptr, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, SWP_NOACTIVATE | SWP_NOZORDER);

			ShowWindow(m_controls->Window, SW_SHOW);

			return 0;
		}

		std::vector<std::pair<char32_t, char32_t>> ParseCustomRangeString() {
			const auto input = XivRes::Unicode::Convert<std::u32string>(GetWindowString(m_controls->CustomRangeEdit));
			size_t next = 0;
			std::vector<std::pair<char32_t, char32_t>> ranges;
			for (size_t i = 0; i < input.size(); i = next + 1) {
				next = input.find_first_of(U",;", i);
				std::u32string_view part;
				if (next == std::u32string::npos) {
					next = input.size() - 1;
					part = std::u32string_view(input).substr(i);
				} else
					part = std::u32string_view(input).substr(i, next - i);

				while (!part.empty() && part.front() < 128 && std::isspace(part.front()))
					part = part.substr(1);
				while (!part.empty() && part.front() < 128 && std::isspace(part.back()))
					part = part.substr(0, part.size() - 1);

				if (part.empty())
					continue;

				if (part.starts_with(U"0x") || part.starts_with(U"0X") || part.starts_with(U"U+") || part.starts_with(U"u+") || part.starts_with(U"\\x") || part.starts_with(U"\\X")) {
					if (const auto sep = part.find_first_of(U"-~:"); sep != std::u32string::npos) {
						auto c1 = std::strtol(XivRes::Unicode::Convert<std::string>(part.substr(2, sep - 2)).c_str(), nullptr, 16);
						auto part2 = part.substr(sep + 1);
						while (!part2.empty() && part2.front() < 128 && std::isspace(part2.front()))
							part2 = part2.substr(1);
						if (part2.starts_with(U"0x") || part2.starts_with(U"0X") || part2.starts_with(U"U+") || part2.starts_with(U"u+") || part2.starts_with(U"\\x") || part2.starts_with(U"\\X"))
							part2 = part2.substr(2);
						auto c2 = std::strtol(XivRes::Unicode::Convert<std::string>(part2).c_str(), nullptr, 16);
						if (c1 < c2)
							ranges.emplace_back(c1, c2);
						else
							ranges.emplace_back(c2, c1);
					} else {
						const auto c = std::strtol(XivRes::Unicode::Convert<std::string>(part.substr(2)).c_str(), nullptr, 16);
						ranges.emplace_back(c, c);
					}
				} else {
					for (const auto c : part)
						ranges.emplace_back(c, c);
				}
			}
			std::sort(ranges.begin(), ranges.end());
			for (size_t i = 1; i < ranges.size();) {
				if (ranges[i - 1].second + 1 >= ranges[i].first) {
					ranges[i - 1].second = (std::max)(ranges[i - 1].second, ranges[i].second);
					ranges.erase(ranges.begin() + i);
				} else
					++i;
			}
			return ranges;
		}

		bool AddNewCodepointRange(char32_t c1, char32_t c2, const std::vector<char32_t>& charVec) {
			const auto newItem = std::make_pair(c1, c2);
			const auto it = std::ranges::lower_bound(m_element.WrapModifiers.Codepoints, newItem);
			if (it != m_element.WrapModifiers.Codepoints.end() && *it == newItem)
				return false;

			const auto newIndex = static_cast<int>(it - m_element.WrapModifiers.Codepoints.begin());
			m_element.WrapModifiers.Codepoints.insert(it, newItem);
			AddCodepointRangeToListBox(newIndex, c1, c2, charVec);
			return true;
		}

		void AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2, const std::vector<char32_t>& charVec) {
			const auto left = std::ranges::lower_bound(charVec, c1);
			const auto right = std::ranges::upper_bound(charVec, c2);
			const auto count = right - left;

			const auto block = std::lower_bound(XivRes::Unicode::UnicodeBlocks::Blocks.begin(), XivRes::Unicode::UnicodeBlocks::Blocks.end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
			if (block != XivRes::Unicode::UnicodeBlocks::Blocks.end() && block->First == c1 && block->Last == c2) {
				if (c1 == c2) {
					ListBox_AddString(m_controls->CodepointsList, std::format(
						L"U+{:04X} {} [{}]",
						static_cast<uint32_t>(c1),
						XivRes::Unicode::Convert<std::wstring>(block->Name),
						XivRes::Unicode::RepresentChar<std::wstring>(c1)
					).c_str());
				} else {
					ListBox_AddString(m_controls->CodepointsList, std::format(
						L"U+{:04X}~{:04X} {} ({}) {} ~ {}",
						static_cast<uint32_t>(c1),
						static_cast<uint32_t>(c2),
						XivRes::Unicode::Convert<std::wstring>(block->Name),
						count,
						XivRes::Unicode::RepresentChar<std::wstring>(c1),
						XivRes::Unicode::RepresentChar<std::wstring>(c2)
					).c_str());
				}
			} else if (c1 == c2) {
				ListBox_AddString(m_controls->CodepointsList, std::format(
					L"U+{:04X} [{}]",
					static_cast<int>(c1),
					XivRes::Unicode::RepresentChar<std::wstring>(c1)
				).c_str());
			} else {
				ListBox_AddString(m_controls->CodepointsList, std::format(
					L"U+{:04X}~{:04X} ({}) {} ~ {}",
					static_cast<uint32_t>(c1),
					static_cast<uint32_t>(c2),
					count,
					XivRes::Unicode::RepresentChar<std::wstring>(c1),
					XivRes::Unicode::RepresentChar<std::wstring>(c2)
				).c_str());
			}
		}

		void RefreshUnicodeBlockSearchResults() {
			const auto input = XivRes::Unicode::Convert<std::string>(GetWindowString(m_controls->UnicodeBlockSearchNameEdit));
			const auto input32 = XivRes::Unicode::Convert<std::u32string>(input);
			ListBox_ResetContent(m_controls->UnicodeBlockSearchResultList);

			const auto searchByChar = Button_GetCheck(m_controls->UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput);

			std::vector<char32_t> charVec(m_element.GetBaseFont()->GetAllCodepoints().begin(), m_element.GetBaseFont()->GetAllCodepoints().end());
			for (const auto& block : XivRes::Unicode::UnicodeBlocks::Blocks) {
				const auto nameView = std::string_view(block.Name);
				const auto it = std::search(nameView.begin(), nameView.end(), input.begin(), input.end(), [](char ch1, char ch2) {
					return std::toupper(ch1) == std::toupper(ch2);
				});
				if (it == nameView.end()) {
					if (searchByChar) {
						auto contains = false;
						for (const auto& c : input32) {
							if (block.First <= c && block.Last >= c) {
								contains = true;
								break;
							}
						}
						if (!contains)
							continue;
					} else
						continue;
				}

				const auto left = std::ranges::lower_bound(charVec, block.First);
				const auto right = std::ranges::upper_bound(charVec, block.Last);
				if (left == right)
					continue;

				ListBox_AddString(m_controls->UnicodeBlockSearchResultList, std::format(
					L"U+{:04X}~{:04X} {} ({}) {} ~ {}",
					static_cast<uint32_t>(block.First),
					static_cast<uint32_t>(block.Last),
					XivRes::Unicode::Convert<std::wstring>(nameView),
					right - left,
					XivRes::Unicode::RepresentChar<std::wstring>(block.First),
					XivRes::Unicode::RepresentChar<std::wstring>(block.Last)
				).c_str());
				ListBox_SetItemData(m_controls->UnicodeBlockSearchResultList, ListBox_GetCount(m_controls->UnicodeBlockSearchResultList) - 1, &block);
			}
		}

		void SetControlsEnabledOrDisabled() {
			switch (m_element.Renderer) {
				case App::Structs::RendererEnum::Empty:
					EnableWindow(m_controls->FontCombo, FALSE);
					EnableWindow(m_controls->FontSizeCombo, TRUE);
					EnableWindow(m_controls->FontWeightCombo, FALSE);
					EnableWindow(m_controls->FontStyleCombo, FALSE);
					EnableWindow(m_controls->FontStretchCombo, FALSE);
					EnableWindow(m_controls->EmptyAscentEdit, TRUE);
					EnableWindow(m_controls->EmptyLineHeightEdit, TRUE);
					EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
					EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
					EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
					EnableWindow(m_controls->AdjustmentBaselineShiftEdit, FALSE);
					EnableWindow(m_controls->AdjustmentLetterSpacingEdit, FALSE);
					EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, FALSE);
					EnableWindow(m_controls->AdjustmentGammaEdit, FALSE);
					EnableWindow(m_controls->CodepointsList, FALSE);
					EnableWindow(m_controls->CodepointsDeleteButton, FALSE);
					EnableWindow(m_controls->CodepointsOverwriteCheck, FALSE);
					EnableWindow(m_controls->UnicodeBlockSearchNameEdit, FALSE);
					EnableWindow(m_controls->UnicodeBlockSearchResultList, FALSE);
					EnableWindow(m_controls->UnicodeBlockSearchAddAll, FALSE);
					EnableWindow(m_controls->UnicodeBlockSearchAdd, FALSE);
					EnableWindow(m_controls->CustomRangeEdit, FALSE);
					EnableWindow(m_controls->CustomRangeAdd, FALSE);
					break;

				case App::Structs::RendererEnum::PrerenderedGameInstallation:
					EnableWindow(m_controls->FontCombo, TRUE);
					EnableWindow(m_controls->FontSizeCombo, TRUE);
					EnableWindow(m_controls->FontWeightCombo, FALSE);
					EnableWindow(m_controls->FontStyleCombo, FALSE);
					EnableWindow(m_controls->FontStretchCombo, FALSE);
					EnableWindow(m_controls->EmptyAscentEdit, FALSE);
					EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
					EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
					EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
					EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
					EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
					EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
					EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
					EnableWindow(m_controls->AdjustmentGammaEdit, FALSE);
					EnableWindow(m_controls->CodepointsList, TRUE);
					EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
					EnableWindow(m_controls->CodepointsOverwriteCheck, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
					EnableWindow(m_controls->CustomRangeEdit, TRUE);
					EnableWindow(m_controls->CustomRangeAdd, TRUE);
					break;

				case App::Structs::RendererEnum::DirectWrite:
					EnableWindow(m_controls->FontCombo, TRUE);
					EnableWindow(m_controls->FontSizeCombo, TRUE);
					EnableWindow(m_controls->FontWeightCombo, TRUE);
					EnableWindow(m_controls->FontStyleCombo, TRUE);
					EnableWindow(m_controls->FontStretchCombo, TRUE);
					EnableWindow(m_controls->EmptyAscentEdit, FALSE);
					EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
					EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
					EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
					EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
					EnableWindow(m_controls->DirectWriteRenderModeCombo, TRUE);
					EnableWindow(m_controls->DirectWriteMeasureModeCombo, TRUE);
					EnableWindow(m_controls->DirectWriteGridFitModeCombo, TRUE);
					EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
					EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
					EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
					EnableWindow(m_controls->AdjustmentGammaEdit, TRUE);
					EnableWindow(m_controls->CodepointsList, TRUE);
					EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
					EnableWindow(m_controls->CodepointsOverwriteCheck, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
					EnableWindow(m_controls->CustomRangeEdit, TRUE);
					EnableWindow(m_controls->CustomRangeAdd, TRUE);
					break;

				case App::Structs::RendererEnum::FreeType:
					EnableWindow(m_controls->FontCombo, TRUE);
					EnableWindow(m_controls->FontSizeCombo, TRUE);
					EnableWindow(m_controls->FontWeightCombo, TRUE);
					EnableWindow(m_controls->FontStyleCombo, TRUE);
					EnableWindow(m_controls->FontStretchCombo, TRUE);
					EnableWindow(m_controls->EmptyAscentEdit, FALSE);
					EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
					EnableWindow(m_controls->FreeTypeNoHintingCheck, TRUE);
					EnableWindow(m_controls->FreeTypeNoBitmapCheck, TRUE);
					EnableWindow(m_controls->FreeTypeForceAutohintCheck, TRUE);
					EnableWindow(m_controls->FreeTypeNoAutohintCheck, TRUE);
					EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
					EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
					EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
					EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
					EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
					EnableWindow(m_controls->AdjustmentGammaEdit, TRUE);
					EnableWindow(m_controls->CodepointsList, TRUE);
					EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
					EnableWindow(m_controls->CodepointsOverwriteCheck, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
					EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
					EnableWindow(m_controls->CustomRangeEdit, TRUE);
					EnableWindow(m_controls->CustomRangeAdd, TRUE);
					break;
			}
		}

		std::vector<IDWriteFontFamilyPtr> m_fontFamilies;

		void RepopulateFontCombobox() {
			ComboBox_ResetContent(m_controls->FontCombo);
			switch (m_element.Renderer) {
				case App::Structs::RendererEnum::Empty:
					break;

				case App::Structs::RendererEnum::PrerenderedGameInstallation:
				{
					std::array<std::wstring, 8> ValidFonts{ {
						L"AXIS",
						L"Jupiter",
						L"JupiterN",
						L"Meidinger",
						L"MiedingerMid",
						L"TrumpGothic",
						L"ChnAXIS",
						L"KrnAXIS",
					} };

					auto anySel = false;
					for (auto& name : ValidFonts) {
						ComboBox_AddString(m_controls->FontCombo, name.c_str());

						auto curNameLower = XivRes::Unicode::Convert<std::wstring>(m_element.Lookup.Name);
						for (auto& c : curNameLower)
							c = std::tolower(c);
						for (auto& c : name)
							c = std::tolower(c);
						if (curNameLower != name)
							continue;
						anySel = true;
						ComboBox_SetCurSel(m_controls->FontCombo, ComboBox_GetCount(m_controls->FontCombo) - 1);
					}
					if (!anySel)
						ComboBox_SetCurSel(m_controls->FontCombo, 0);
					break;
				}

				case App::Structs::RendererEnum::DirectWrite:
				case App::Structs::RendererEnum::FreeType:
				{
					using namespace XivRes::FontGenerator;

					IDWriteFactory3Ptr factory;
					SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(&factory)));

					IDWriteFontCollectionPtr coll;
					SuccessOrThrow(factory->GetSystemFontCollection(&coll));

					m_fontFamilies.clear();
					std::vector<std::wstring> names;
					for (uint32_t i = 0, i_ = coll->GetFontFamilyCount(); i < i_; i++) {
						IDWriteFontFamilyPtr family;
						IDWriteLocalizedStringsPtr strings;

						if (FAILED(coll->GetFontFamily(i, &family)))
							continue;

						if (FAILED(family->GetFamilyNames(&strings)))
							continue;

						uint32_t index;
						BOOL exists;
						if (FAILED(strings->FindLocaleName(L"en-us", &index, &exists)))
							continue;
						if (exists)
							index = 0;

						uint32_t length;
						if (FAILED(strings->GetStringLength(index, &length)))
							continue;

						std::wstring res(length + 1, L'\0');
						if (FAILED(strings->GetString(index, &res[0], length + 1)))
							continue;
						res.resize(length);

						std::wstring resLower = res;
						for (auto& c : resLower)
							c = std::tolower(c);

						const auto insertAt = std::ranges::lower_bound(names, resLower) - names.begin();

						ComboBox_InsertString(m_controls->FontCombo, insertAt, res.c_str());
						m_fontFamilies.insert(m_fontFamilies.begin() + insertAt, std::move(family));
						names.insert(names.begin() + insertAt, std::move(resLower));
					}

					auto curNameLower = XivRes::Unicode::Convert<std::wstring>(m_element.Lookup.Name);
					for (auto& c : curNameLower)
						c = std::tolower(c);
					if (const auto it = std::ranges::lower_bound(names, curNameLower); it != names.end() && *it == curNameLower)
						ComboBox_SetCurSel(m_controls->FontCombo, it - names.begin());
					else
						ComboBox_SetCurSel(m_controls->FontCombo, -1);
					break;
				}
				default:
					break;
			}

			RepopulateFontSubComboBox();
		}

		void RepopulateFontSubComboBox() {
			ComboBox_ResetContent(m_controls->FontWeightCombo);
			ComboBox_ResetContent(m_controls->FontStyleCombo);
			ComboBox_ResetContent(m_controls->FontStretchCombo);
			ComboBox_ResetContent(m_controls->FontSizeCombo);

			auto restoreFontSizeInCommonWay = true;
			switch (m_element.Renderer) {
				case App::Structs::RendererEnum::PrerenderedGameInstallation:
				{
					std::vector<float> sizes;
					switch (ComboBox_GetCurSel(m_controls->FontCombo)) {
						case 0: // AXIS
							sizes = { 9.6f, 12.f, 14.f, 18.f, 36.f };
							break;

						case 1: // Jupiter
							sizes = { 16.f, 20.f, 23.f, 46.f };
							break;

						case 2: // JupiterN
							sizes = { 45.f, 90.f };
							break;

						case 3: // Meidinger
							sizes = { 16.f, 20.f, 40.f };
							break;

						case 4: // MiedingerMid
							sizes = { 10.f, 12.f, 14.f, 18.f, 36.f };
							break;

						case 5: // TrumpGothic
							sizes = { 18.4f, 23.f, 34.f, 68.f };
							break;

						case 6: // ChnAXIS
							sizes = { 12.f, 14.f, 18.f, 36.f };
							break;

						case 7: // KrnAXIS
							sizes = { 12.f, 14.f, 18.f, 36.f };
							break;
					}

					auto closestIndex = std::ranges::min_element(sizes, [prevSize = m_element.Size](const auto& n1, const auto& n2) { return std::fabs(n1 - prevSize) < std::fabs(n2 - prevSize); }) - sizes.begin();

					for (size_t i = 0; i < sizes.size(); i++) {
						ComboBox_AddString(m_controls->FontSizeCombo, std::format(L"{:g}", sizes[i]).c_str());
						if (i == closestIndex) {
							ComboBox_SetCurSel(m_controls->FontSizeCombo, i);
							m_element.Size = sizes[i];
						}
					}

					restoreFontSizeInCommonWay = false;

					ComboBox_AddString(m_controls->FontWeightCombo, L"400 (Normal/Regular)");
					ComboBox_AddString(m_controls->FontStyleCombo, L"Normal");
					ComboBox_AddString(m_controls->FontStretchCombo, L"Normal");
					ComboBox_SetCurSel(m_controls->FontWeightCombo, 0);
					ComboBox_SetCurSel(m_controls->FontStyleCombo, 0);
					ComboBox_SetCurSel(m_controls->FontStretchCombo, 0);
					m_element.Lookup.Weight = DWRITE_FONT_WEIGHT_NORMAL;
					m_element.Lookup.Style = DWRITE_FONT_STYLE_NORMAL;
					m_element.Lookup.Stretch = DWRITE_FONT_STRETCH_NORMAL;
					break;
				}

				case App::Structs::RendererEnum::DirectWrite:
				case App::Structs::RendererEnum::FreeType:
				{
					const auto curSel = (std::max)(0, (std::min)(static_cast<int>(m_fontFamilies.size() - 1), ComboBox_GetCurSel(m_controls->FontCombo)));
					const auto& family = m_fontFamilies[curSel];
					std::set<DWRITE_FONT_WEIGHT> weights;
					std::set<DWRITE_FONT_STYLE> styles;
					std::set<DWRITE_FONT_STRETCH> stretches;
					for (uint32_t i = 0, i_ = family->GetFontCount(); i < i_; i++) {
						IDWriteFontPtr font;
						if (FAILED(family->GetFont(i, &font)))
							continue;

						weights.insert(font->GetWeight());
						styles.insert(font->GetStyle());
						stretches.insert(font->GetStretch());
					}

					auto closestWeight = *weights.begin();
					auto closestStyle = *styles.begin();
					auto closestStretch = *stretches.begin();
					for (const auto v : weights) {
						if (std::abs(static_cast<int>(m_element.Lookup.Weight) - static_cast<int>(v)) < std::abs(static_cast<int>(m_element.Lookup.Weight) - static_cast<int>(closestWeight)))
							closestWeight = v;
					}
					for (const auto v : styles) {
						if (std::abs(static_cast<int>(m_element.Lookup.Style) - static_cast<int>(v)) < std::abs(static_cast<int>(m_element.Lookup.Style) - static_cast<int>(closestStyle)))
							closestStyle = v;
					}
					for (const auto v : stretches) {
						if (std::abs(static_cast<int>(m_element.Lookup.Stretch) - static_cast<int>(v)) < std::abs(static_cast<int>(m_element.Lookup.Stretch) - static_cast<int>(closestStretch)))
							closestStretch = v;
					}

					for (const auto v : weights) {
						switch (v) {
							case 100: ComboBox_AddString(m_controls->FontWeightCombo, L"100 (Thin)"); break;
							case 200: ComboBox_AddString(m_controls->FontWeightCombo, L"200 (Extra Light/Ultra Light)"); break;
							case 300: ComboBox_AddString(m_controls->FontWeightCombo, L"300 (Light)"); break;
							case 350: ComboBox_AddString(m_controls->FontWeightCombo, L"350 (Semi Light)"); break;
							case 400: ComboBox_AddString(m_controls->FontWeightCombo, L"400 (Normal/Regular)"); break;
							case 500: ComboBox_AddString(m_controls->FontWeightCombo, L"500 (Medium)"); break;
							case 600: ComboBox_AddString(m_controls->FontWeightCombo, L"600 (Semi Bold/Demibold)"); break;
							case 700: ComboBox_AddString(m_controls->FontWeightCombo, L"700 (Bold)"); break;
							case 800: ComboBox_AddString(m_controls->FontWeightCombo, L"800 (Extra Bold/Ultra Bold)"); break;
							case 900: ComboBox_AddString(m_controls->FontWeightCombo, L"900 (Black/Heavy)"); break;
							case 950: ComboBox_AddString(m_controls->FontWeightCombo, L"950 (Extra Black/Ultra Black)"); break;
							default: ComboBox_AddString(m_controls->FontWeightCombo, std::format(L"{}", static_cast<int>(v)).c_str());
						}

						if (v == closestWeight) {
							ComboBox_SetCurSel(m_controls->FontWeightCombo, ComboBox_GetCount(m_controls->FontWeightCombo) - 1);
							m_element.Lookup.Weight = v;
						}
					}

					for (const auto v : styles) {
						switch (v) {
							case DWRITE_FONT_STYLE_NORMAL: ComboBox_AddString(m_controls->FontStyleCombo, L"Normal"); break;
							case DWRITE_FONT_STYLE_OBLIQUE: ComboBox_AddString(m_controls->FontStyleCombo, L"Oblique"); break;
							case DWRITE_FONT_STYLE_ITALIC: ComboBox_AddString(m_controls->FontStyleCombo, L"Italic"); break;
							default: continue;
						}

						if (v == closestStyle) {
							ComboBox_SetCurSel(m_controls->FontStyleCombo, ComboBox_GetCount(m_controls->FontStyleCombo) - 1);
							m_element.Lookup.Style = v;
						}
					}

					for (const auto v : stretches) {
						switch (v) {
							case DWRITE_FONT_STRETCH_ULTRA_CONDENSED: ComboBox_AddString(m_controls->FontStretchCombo, L"Ultra Condensed"); break;
							case DWRITE_FONT_STRETCH_EXTRA_CONDENSED: ComboBox_AddString(m_controls->FontStretchCombo, L"Extra Condensed"); break;
							case DWRITE_FONT_STRETCH_CONDENSED: ComboBox_AddString(m_controls->FontStretchCombo, L"Condensed"); break;
							case DWRITE_FONT_STRETCH_SEMI_CONDENSED: ComboBox_AddString(m_controls->FontStretchCombo, L"Semi Condensed"); break;
							case DWRITE_FONT_STRETCH_NORMAL: ComboBox_AddString(m_controls->FontStretchCombo, L"Normal"); break;
							case DWRITE_FONT_STRETCH_SEMI_EXPANDED: ComboBox_AddString(m_controls->FontStretchCombo, L"Semi Expanded"); break;
							case DWRITE_FONT_STRETCH_EXPANDED: ComboBox_AddString(m_controls->FontStretchCombo, L"Expanded"); break;
							case DWRITE_FONT_STRETCH_EXTRA_EXPANDED: ComboBox_AddString(m_controls->FontStretchCombo, L"Extra Expanded"); break;
							case DWRITE_FONT_STRETCH_ULTRA_EXPANDED: ComboBox_AddString(m_controls->FontStretchCombo, L"Ultra Expanded"); break;
							default: continue;
						}

						if (v == closestStretch) {
							ComboBox_SetCurSel(m_controls->FontStretchCombo, ComboBox_GetCount(m_controls->FontStretchCombo) - 1);
							m_element.Lookup.Stretch = v;
						}
					}
					break;
				}

				default:
					break;
			}

			if (restoreFontSizeInCommonWay) {
				constexpr std::array<float, 31> sizes{ {
					8.f, 9.f, 9.6f, 10.f, 11.f, 12.f, 13.f, 14.f, 15.f, 16.f, 17.f, 18.f, 18.4f, 19.f,
					20.f, 21.f, 22.f, 23.f, 24.f, 26.f, 28.f, 30.f, 32.f, 34.f, 36.f, 38.f, 40.f, 45.f, 46.f, 68.f, 90.f } };

				for (const auto size : sizes)
					ComboBox_AddString(m_controls->FontSizeCombo, std::format(L"{:g}", size).c_str());

				auto fontSizeRestored = false;
				const auto prevSize = m_element.Size;
				for (size_t i = 0; i < sizes.size(); i++) {
					ComboBox_AddString(m_controls->FontSizeCombo, std::format(L"{:g}", sizes[i]).c_str());
					if (sizes[i] >= m_element.Size && !fontSizeRestored) {
						ComboBox_SetCurSel(m_controls->FontSizeCombo, i);
						m_element.Size = sizes[i];
						fontSizeRestored = true;
					}
				}
				if (!fontSizeRestored)
					ComboBox_SetCurSel(m_controls->FontSizeCombo, ComboBox_GetCount(m_controls->FontSizeCombo));
				ComboBox_SetText(m_controls->FontSizeCombo, std::format(L"{:g}", prevSize).c_str());
			}
		}

		void OnBaseFontChanged() {
			m_bBaseFontChanged = true;
			m_element.OnFontCreateParametersChange();
			if (m_onFontChanged)
				m_onFontChanged();
		}

		void OnWrappedFontChanged() {
			m_bWrappedFontChanged = true;
			m_element.OnFontWrappingParametersChange();
			if (m_onFontChanged)
				m_onFontChanged();
		}

		INT_PTR DlgProc(UINT message, WPARAM wParam, LPARAM lParam) {
			switch (message) {
				case WM_INITDIALOG:
					return Dialog_OnInitDialog();
				case WM_COMMAND:
				{
					switch (LOWORD(wParam)) {
						case IDOK: return OkButton_OnCommand(HIWORD(wParam));
						case IDCANCEL: return CancelButton_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT_RENDERER: return FontRendererCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT: return FontCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT_SIZE: return FontSizeCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT_WEIGHT: return FontWeightCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT_STYLE: return FontStyleCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_FONT_STRETCH: return FontStretchCombo_OnCommand(HIWORD(wParam));
						case IDC_EDIT_EMPTY_ASCENT: return EmptyAscentEdit_OnChange(HIWORD(wParam));
						case IDC_EDIT_EMPTY_LINEHEIGHT: return EmptyLineHeightEdit_OnChange(HIWORD(wParam));
						case IDC_CHECK_FREETYPE_NOHINTING:
						case IDC_CHECK_FREETYPE_NOBITMAP:
						case IDC_CHECK_FREETYPE_FORCEAUTOHINT:
						case IDC_CHECK_FREETYPE_NOAUTOHINT: return FreeTypeCheck_OnClick(HIWORD(wParam), LOWORD(wParam), reinterpret_cast<HWND>(lParam));
						case IDC_COMBO_DIRECTWRITE_RENDERMODE: return DirectWriteRenderModeCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_DIRECTWRITE_MEASUREMODE: return DirectWriteMeasureModeCombo_OnCommand(HIWORD(wParam));
						case IDC_COMBO_DIRECTWRITE_GRIDFITMODE: return DirectWriteGridFitModeCombo_OnCommand(HIWORD(wParam));
						case IDC_EDIT_ADJUSTMENT_BASELINESHIFT: return AdjustmentBaselineShiftEdit_OnChange(HIWORD(wParam));
						case IDC_EDIT_ADJUSTMENT_LETTERSPACING: return AdjustmentLetterSpacingEdit_OnChange(HIWORD(wParam));
						case IDC_EDIT_ADJUSTMENT_HORIZONTALOFFSET: return AdjustmentHorizontalOffsetEdit_OnChange(HIWORD(wParam));
						case IDC_EDIT_ADJUSTMENT_GAMMA: return AdjustmentGammaEdit_OnChange(HIWORD(wParam));
						case IDC_LIST_CODEPOINTS: return CodepointsList_OnCommand(HIWORD(wParam));
						case IDC_BUTTON_CODEPOINTS_DELETE: return CodepointsDeleteButton_OnCommand(HIWORD(wParam));
						case IDC_CHECK_CODEPOINTS_OVERWRITE: return CodepointsOverwriteCheck_OnCommand(HIWORD(wParam));
						case IDC_EDIT_UNICODEBLOCKS_SEARCH: return UnicodeBlockSearchNameEdit_OnCommand(HIWORD(wParam));
						case IDC_CHECK_UNICODEBLOCKS_SHOWBLOCKSWITHANYOFCHARACTERSINPUT: return UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(HIWORD(wParam));
						case IDC_LIST_UNICODEBLOCKS_SEARCHRESULTS: return UnicodeBlockSearchResultList_OnCommand(HIWORD(wParam));
						case IDC_BUTTON_UNICODEBLOCKS_ADDALL: return UnicodeBlockSearchAddAll_OnCommand(HIWORD(wParam));
						case IDC_BUTTON_UNICODEBLOCKS_ADD: return UnicodeBlockSearchAdd_OnCommand(HIWORD(wParam));
						case IDC_EDIT_ADDCUSTOMRANGE_INPUT: return CustomRangeEdit_OnCommand(HIWORD(wParam));
						case IDC_BUTTON_ADDCUSTOMRANGE_ADD: return CustomRangeAdd_OnCommand(HIWORD(wParam));
					}
					return 0;
				}
				case WM_CLOSE:
				{
					EndDialog(m_controls->Window, 0);
					m_bOpened = false;
					return 0;
				}
				case WM_DESTROY:
				{
					m_bOpened = false;
					return 0;
				}
				case WmFontSizeTextChanged:
				{
					if (const auto f = GetWindowFloat(m_controls->FontSizeCombo); f != m_element.Size) {
						m_element.Size = f;
						OnBaseFontChanged();
					}
					return 0;
				}
			}
			return 0;
		}

		static INT_PTR __stdcall DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
			if (message == WM_INITDIALOG) {
				auto& params = *reinterpret_cast<FaceElementEditorDialog*>(lParam);
				params.m_controls = new ControlStruct{ hwnd };
				SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&params));
				return params.DlgProc(message, wParam, lParam);
			} else {
				return reinterpret_cast<FaceElementEditorDialog*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA))->DlgProc(message, wParam, lParam);
			}
			return 0;
		}
	};
}
