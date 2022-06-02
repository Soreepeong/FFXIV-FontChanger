#include "pch.h"
#include "FaceElementEditorDialog.h"
#include "resource.h"

struct App::FaceElementEditorDialog::ControlStruct {
	HWND Window;
	HWND OkButton = GetDlgItem(Window, IDOK);
	HWND CancelButton = GetDlgItem(Window, IDCANCEL);
	HWND FontRendererCombo = GetDlgItem(Window, IDC_COMBO_FONT_RENDERER);
	HWND FontCombo = GetDlgItem(Window, IDC_COMBO_FONT);
	HWND FontSizeEdit = GetDlgItem(Window, IDC_EDIT_FONT_SIZE);
	HWND FontWeightCombo = GetDlgItem(Window, IDC_COMBO_FONT_WEIGHT);
	HWND FontStyleCombo = GetDlgItem(Window, IDC_COMBO_FONT_STYLE);
	HWND FontStretchCombo = GetDlgItem(Window, IDC_COMBO_FONT_STRETCH);
	HWND EmptyAscentEdit = GetDlgItem(Window, IDC_EDIT_EMPTY_ASCENT);
	HWND EmptyLineHeightEdit = GetDlgItem(Window, IDC_EDIT_EMPTY_LINEHEIGHT);
	HWND FreeTypeNoHintingCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOHINTING);
	HWND FreeTypeNoBitmapCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOBITMAP);
	HWND FreeTypeForceAutohintCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_FORCEAUTOHINT);
	HWND FreeTypeNoAutohintCheck = GetDlgItem(Window, IDC_CHECK_FREETYPE_NOAUTOHINT);
	HWND FreeTypeRenderModeCombo = GetDlgItem(Window, IDC_COMBO_FREETYPE_RENDERMODE);
	HWND DirectWriteRenderModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_RENDERMODE);
	HWND DirectWriteMeasureModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_MEASUREMODE);
	HWND DirectWriteGridFitModeCombo = GetDlgItem(Window, IDC_COMBO_DIRECTWRITE_GRIDFITMODE);
	HWND AdjustmentBaselineShiftEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_BASELINESHIFT);
	HWND AdjustmentLetterSpacingEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_LETTERSPACING);
	HWND AdjustmentHorizontalOffsetEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_HORIZONTALOFFSET);
	HWND AdjustmentGammaEdit = GetDlgItem(Window, IDC_EDIT_ADJUSTMENT_GAMMA);
	HWND CodepointsList = GetDlgItem(Window, IDC_LIST_CODEPOINTS);
	HWND CodepointsClearButton = GetDlgItem(Window, IDC_BUTTON_CODEPOINTS_CLEAR);
	HWND CodepointsDeleteButton = GetDlgItem(Window, IDC_BUTTON_CODEPOINTS_DELETE);
	HWND CodepointsMergeModeCombo = GetDlgItem(Window, IDC_COMBO_CODEPOINTS_MERGEMODE);
	HWND UnicodeBlockSearchNameEdit = GetDlgItem(Window, IDC_EDIT_UNICODEBLOCKS_SEARCH);
	HWND UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput = GetDlgItem(Window, IDC_CHECK_UNICODEBLOCKS_SHOWBLOCKSWITHANYOFCHARACTERSINPUT);
	HWND UnicodeBlockSearchResultList = GetDlgItem(Window, IDC_LIST_UNICODEBLOCKS_SEARCHRESULTS);
	HWND UnicodeBlockSearchSelectedPreviewEdit = GetDlgItem(Window, IDC_EDIT_UNICODEBLOCKS_RANGEPREVIEW);
	HWND UnicodeBlockSearchAddAll = GetDlgItem(Window, IDC_BUTTON_UNICODEBLOCKS_ADDALL);
	HWND UnicodeBlockSearchAdd = GetDlgItem(Window, IDC_BUTTON_UNICODEBLOCKS_ADD);
	HWND CustomRangeEdit = GetDlgItem(Window, IDC_EDIT_ADDCUSTOMRANGE_INPUT);
	HWND CustomRangePreview = GetDlgItem(Window, IDC_EDIT_ADDCUSTOMRANGE_PREVIEW);
	HWND CustomRangeAdd = GetDlgItem(Window, IDC_BUTTON_ADDCUSTOMRANGE_ADD);
	HWND TransformationMatrixM11Edit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_M11);
	HWND TransformationMatrixM12Edit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_M12);
	HWND TransformationMatrixM21Edit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_M21);
	HWND TransformationMatrixM22Edit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_M22);
	HWND TransformationMatrixDXEdit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_DX);
	HWND TransformationMatrixDYEdit = GetDlgItem(Window, IDC_EDIT_TRANSFORMATIONMATRIX_DY);
	HWND TransformationMatrixHelp = GetDlgItem(Window, IDC_BUTTON_TRANSFORMATIONMATRIX_HELP);
	HWND TransformationMatrixReset = GetDlgItem(Window, IDC_BUTTON_TRANSFORMATIONMATRIX_RESET);
};

App::FaceElementEditorDialog::FaceElementEditorDialog(HWND hParentWnd, App::Structs::FaceElement& element, std::function<void()> onFontChanged)
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

App::FaceElementEditorDialog::~FaceElementEditorDialog() {
	delete m_controls;
}

bool App::FaceElementEditorDialog::IsOpened() const {
	return m_bOpened;
}

void App::FaceElementEditorDialog::Activate() const {
	BringWindowToTop(m_controls->Window);
}

bool App::FaceElementEditorDialog::ConsumeDialogMessage(MSG& msg) {
	return m_controls && IsDialogMessage(m_controls->Window, &msg);
}

template<typename T>
bool App::FaceElementEditorDialog::TryEvaluate(const std::wstring& wstr, T& res, bool silent) {
	exprtk::expression<double> expr;
	exprtk::parser<double> parser;
	if (parser.compile(xivres::util::unicode::convert<std::string>(std::wstring_view(wstr).substr(1)), expr)) {
		if constexpr (std::is_integral_v<T>)
			res = static_cast<T>(std::round(expr.value()));
		else
			res = static_cast<T>(expr.value());
		return true;

	} else {
		std::string errors;
		if (parser.error_count() == 1)
			errors = "Failed to evaluate expression due to following error.";
		else
			errors = "Failed to evaluate expression due to following errors.";
		for (size_t i = 0; i < parser.error_count(); i++) {
			const auto error = parser.get_error(i);
			errors += std::format("\n* {:02} [{}] {}",
				error.token.position,
				exprtk::parser_error::to_str(error.mode),
				error.diagnostic);
		}

		if (!silent)
			MessageBoxW(m_controls->Window, xivres::util::unicode::convert<std::wstring>(errors).c_str(), L"Error", MB_OK | MB_ICONWARNING);
		return false;
	}
}

template<typename T>
bool App::FaceElementEditorDialog::TryGetOrEvaluateValueInto(HWND hwnd, T& res, const T& originalValue) {
	auto wstr = GetWindowString(hwnd, true);
	T newValue = originalValue;
	if (!wstr.empty()) {
		if (!wstr.starts_with(L'='))
			newValue = static_cast<T>(std::wcstod(wstr.c_str(), nullptr));
		else
			void(TryEvaluate(wstr, newValue, true));
	}

	if (newValue == res)
		return false;

	res = newValue;
	return true;
}

INT_PTR App::FaceElementEditorDialog::OkButton_OnCommand(uint16_t notiCode) {
	EndDialog(m_controls->Window, 0);
	m_bOpened = false;
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CancelButton_OnCommand(uint16_t notiCode) {
	m_element = std::move(m_elementOriginal);
	EndDialog(m_controls->Window, 0);
	m_bOpened = false;

	if (m_bBaseFontChanged)
		OnBaseFontChanged();
	else if (m_bWrappedFontChanged)
		OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontRendererCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	m_element.Renderer = static_cast<App::Structs::RendererEnum>(ComboBox_GetCurSel(m_controls->FontRendererCombo));
	SetControlsEnabledOrDisabled();
	RepopulateFontCombobox();
	OnBaseFontChanged();
	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	RepopulateFontSubComboBox();
	m_element.Lookup.Name = xivres::util::unicode::convert<std::string>(GetWindowString(m_controls->FontCombo));
	OnBaseFontChanged();
	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontSizeEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->FontSizeEdit, m_element.Size, m_elementOriginal.Size))
		OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontWeightCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto w = static_cast<DWRITE_FONT_WEIGHT>(GetWindowNumber<int>(m_controls->FontWeightCombo)); w != m_element.Lookup.Weight) {
		m_element.Lookup.Weight = w;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontStyleCombo_OnCommand(uint16_t notiCode) {
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

INT_PTR App::FaceElementEditorDialog::FontStretchCombo_OnCommand(uint16_t notiCode) {
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

INT_PTR App::FaceElementEditorDialog::EmptyAscentEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->EmptyAscentEdit, m_element.RendererSpecific.Empty.Ascent, m_elementOriginal.RendererSpecific.Empty.Ascent))
		OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::EmptyLineHeightEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->EmptyLineHeightEdit, m_element.RendererSpecific.Empty.LineHeight, m_elementOriginal.RendererSpecific.Empty.LineHeight))
		OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FreeTypeCheck_OnCommand(uint16_t notiCode, uint16_t id, HWND hWnd) {
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

INT_PTR App::FaceElementEditorDialog::FreeTypeRenderModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<FT_Render_Mode>(ComboBox_GetCurSel(m_controls->FreeTypeRenderModeCombo));
		v != m_element.RendererSpecific.FreeType.RenderMode) {
		m_element.RendererSpecific.FreeType.RenderMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteRenderModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<DWRITE_RENDERING_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteRenderModeCombo));
		v != m_element.RendererSpecific.DirectWrite.RenderMode) {
		m_element.RendererSpecific.DirectWrite.RenderMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteMeasureModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<DWRITE_MEASURING_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteMeasureModeCombo));
		v != m_element.RendererSpecific.DirectWrite.MeasureMode) {
		m_element.RendererSpecific.DirectWrite.MeasureMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteGridFitModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<DWRITE_GRID_FIT_MODE>(ComboBox_GetCurSel(m_controls->DirectWriteGridFitModeCombo));
		v != m_element.RendererSpecific.DirectWrite.GridFitMode) {
		m_element.RendererSpecific.DirectWrite.GridFitMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::AdjustmentBaselineShiftEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->AdjustmentBaselineShiftEdit, m_element.WrapModifiers.BaselineShift, m_elementOriginal.WrapModifiers.BaselineShift))
		OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::AdjustmentLetterSpacingEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->AdjustmentLetterSpacingEdit, m_element.WrapModifiers.LetterSpacing, m_elementOriginal.WrapModifiers.LetterSpacing))
		OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::AdjustmentHorizontalOffsetEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->AdjustmentHorizontalOffsetEdit, m_element.WrapModifiers.HorizontalOffset, m_elementOriginal.WrapModifiers.HorizontalOffset))
		OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::AdjustmentGammaEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (TryGetOrEvaluateValueInto(m_controls->AdjustmentGammaEdit, m_element.Gamma, m_elementOriginal.Gamma))
		OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::TransformationMatrixEdit_OnCommand(int index, uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	if (
		TryGetOrEvaluateValueInto(
			(&m_controls->TransformationMatrixM11Edit)[index],
			(&m_element.TransformationMatrix.M11)[index],
			(&m_elementOriginal.TransformationMatrix.M11)[index])
		) {
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::TransformationMatrixHelpButton_OnCommand(uint16_t notiCode) {
	ShellExecuteW(m_controls->Window, L"open", L"https://en.wikipedia.org/wiki/Transformation_matrix#Examples_in_2_dimensions", nullptr, nullptr, SW_SHOW);
	return 0;
}

INT_PTR App::FaceElementEditorDialog::TransformationMatrixResetButton_OnCommand(uint16_t notiCode) {
	SetWindowNumber(m_controls->TransformationMatrixM11Edit, m_element.TransformationMatrix.M11 = 1.f);
	SetWindowNumber(m_controls->TransformationMatrixM12Edit, m_element.TransformationMatrix.M12 = 0.f);
	SetWindowNumber(m_controls->TransformationMatrixM21Edit, m_element.TransformationMatrix.M21 = 0.f);
	SetWindowNumber(m_controls->TransformationMatrixM22Edit, m_element.TransformationMatrix.M22 = 1.f);
	OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CustomRangeEdit_OnCommand(uint16_t notiCode) {
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
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			);
		} else {
			description += std::format(
				L"U+{:04X}~{:04X} {}~{}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			);
		}
	}

	Edit_SetText(m_controls->CustomRangePreview, &description[0]);

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CustomRangeAdd_OnCommand(uint16_t notiCode) {
	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());

	auto changed = false;
	for (const auto& [c1, c2] : ParseCustomRangeString())
		changed |= AddNewCodepointRange(c1, c2, charVec);

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsList_OnCommand(uint16_t notiCode) {
	if (notiCode == LBN_DBLCLK)
		return CodepointsDeleteButton_OnCommand(BN_CLICKED);

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsClearButton_OnCommand(uint16_t notiCode) {
	ListBox_ResetContent(m_controls->CodepointsList);
	OnWrappedFontChanged();
	m_element.WrapModifiers.Codepoints.clear();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsDeleteButton_OnCommand(uint16_t notiCode) {
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

INT_PTR App::FaceElementEditorDialog::CodepointsMergeModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<xivres::fontgen::codepoint_merge_mode>(ComboBox_GetCurSel(m_controls->CodepointsMergeModeCombo));
		v != m_element.MergeMode) {
		m_element.MergeMode = v;
		OnWrappedFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchNameEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(uint16_t notiCode) {
	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchResultList_OnCommand(uint16_t notiCode) {
	if (notiCode == LBN_SELCHANGE || notiCode == LBN_SELCANCEL) {
		std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
		if (selItems.empty())
			return 0;

		ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), &selItems[0]);

		std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
		std::wstring containingChars;

		containingChars.reserve(8192);

		for (const auto itemIndex : selItems) {
			const auto& block = *reinterpret_cast<xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));

			for (auto it = std::ranges::lower_bound(charVec, block.First), it_ = std::ranges::upper_bound(charVec, block.Last); it != it_; ++it) {
				xivres::util::unicode::represent_codepoint(containingChars, *it);
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

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchAddAll_OnCommand(uint16_t notiCode) {
	auto changed = false;
	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
	for (int i = 0, i_ = ListBox_GetCount(m_controls->UnicodeBlockSearchResultList); i < i_; i++) {
		const auto& block = *reinterpret_cast<const xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, i));
		changed |= AddNewCodepointRange(block.First, block.Last, charVec);
	}

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchAdd_OnCommand(uint16_t notiCode) {
	std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
	if (selItems.empty())
		return 0;

	ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), &selItems[0]);

	auto changed = false;
	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
	for (const auto itemIndex : selItems) {
		const auto& block = *reinterpret_cast<const xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));
		changed |= AddNewCodepointRange(block.First, block.Last, charVec);
	}

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::ExpressionHelpButton_OnCommand(uint16_t notiCode) {
	ShellExecuteW(m_controls->Window, L"open", L"https://www.partow.net/programming/exprtk/index.html", nullptr, nullptr, SW_SHOW);
	return 0;
}

INT_PTR App::FaceElementEditorDialog::Dialog_OnInitDialog() {
	m_bOpened = true;
	SetControlsEnabledOrDisabled();
	RepopulateFontCombobox();
	RefreshUnicodeBlockSearchResults();
	ComboBox_AddString(m_controls->FontRendererCombo, L"Empty");
	ComboBox_AddString(m_controls->FontRendererCombo, L"Prerendered (Game)");
	ComboBox_AddString(m_controls->FontRendererCombo, L"DirectWrite");
	ComboBox_AddString(m_controls->FontRendererCombo, L"FreeType");
	ComboBox_SetCurSel(m_controls->FontRendererCombo, static_cast<int>(m_element.Renderer));
	ComboBox_SetText(m_controls->FontCombo, xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name).c_str());
	SetWindowNumber(m_controls->EmptyAscentEdit, m_element.RendererSpecific.Empty.Ascent);
	SetWindowNumber(m_controls->EmptyLineHeightEdit, m_element.RendererSpecific.Empty.LineHeight);
	Button_SetCheck(m_controls->FreeTypeNoHintingCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_HINTING) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeNoBitmapCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_BITMAP) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeForceAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_FORCE_AUTOHINT) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeNoAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_AUTOHINT) ? TRUE : FALSE);
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"Normal");
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"Light");
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"Mono");
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"LCD (Probably not what you want)");
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"LCD_V (Probably not what you want)");
	ComboBox_AddString(m_controls->FreeTypeRenderModeCombo, L"SDF");
	ComboBox_SetCurSel(m_controls->FreeTypeRenderModeCombo, static_cast<int>(m_element.RendererSpecific.FreeType.RenderMode));
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

	SetWindowNumber(m_controls->AdjustmentBaselineShiftEdit, m_element.WrapModifiers.BaselineShift);
	SetWindowNumber(m_controls->AdjustmentLetterSpacingEdit, m_element.WrapModifiers.LetterSpacing);
	SetWindowNumber(m_controls->AdjustmentHorizontalOffsetEdit, m_element.WrapModifiers.HorizontalOffset);
	SetWindowNumber(m_controls->AdjustmentGammaEdit, m_element.Gamma);

	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
	for (int i = 0, i_ = static_cast<int>(m_element.WrapModifiers.Codepoints.size()); i < i_; i++)
		AddCodepointRangeToListBox(i, m_element.WrapModifiers.Codepoints[i].first, m_element.WrapModifiers.Codepoints[i].second, charVec);

	ComboBox_AddString(m_controls->CodepointsMergeModeCombo, L"Add new glyphs");
	ComboBox_AddString(m_controls->CodepointsMergeModeCombo, L"Add all glyphs");
	ComboBox_AddString(m_controls->CodepointsMergeModeCombo, L"Replace existing glyphs");
	ComboBox_SetCurSel(m_controls->CodepointsMergeModeCombo, static_cast<int>(m_element.MergeMode));

	SetWindowNumber(m_controls->TransformationMatrixM11Edit, m_element.TransformationMatrix.M11);
	SetWindowNumber(m_controls->TransformationMatrixM12Edit, m_element.TransformationMatrix.M12);
	SetWindowNumber(m_controls->TransformationMatrixM21Edit, m_element.TransformationMatrix.M21);
	SetWindowNumber(m_controls->TransformationMatrixM22Edit, m_element.TransformationMatrix.M22);


	for (const auto& controlHwnd : {
		m_controls->EmptyAscentEdit,
		m_controls->EmptyLineHeightEdit,
		m_controls->AdjustmentBaselineShiftEdit,
		m_controls->AdjustmentLetterSpacingEdit,
		m_controls->AdjustmentHorizontalOffsetEdit
		}) {
		SetWindowSubclass(controlHwnd, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
			const auto minValue = -128, maxValue = 127;
			if (msg == WM_KEYDOWN && wParam == VK_DOWN && !GetWindowString(hWnd).starts_with(L"=")) {
				SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<int>(hWnd) + 1));
				return 0;

			} else if (msg == WM_KEYDOWN && wParam == VK_UP && !GetWindowString(hWnd).starts_with(L"=")) {
				SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<int>(hWnd) - 1));
				return 0;

			} else if (msg == WM_GETDLGCODE && wParam == VK_RETURN && GetWindowString(hWnd).starts_with(L"=")) {
				return DefSubclassProc(hWnd, msg, wParam, lParam) | DLGC_WANTALLKEYS;

			} else if (msg == WM_KEYDOWN && wParam == VK_RETURN) {
				if (const auto wstr = GetWindowString(hWnd); wstr.starts_with(L"=")) {
					if (int r; reinterpret_cast<FaceElementEditorDialog*>(dwRefData)->TryEvaluate(wstr, r))
						SetWindowNumber(hWnd, (std::min)(maxValue, (std::max)(minValue, r)));
					return 0;
				}
			}

			return DefSubclassProc(hWnd, msg, wParam, lParam);
		}, 1, reinterpret_cast<DWORD_PTR>(this));
	}

	for (const auto& controlHwnd : {
		m_controls->AdjustmentGammaEdit,
		}) {
		SetWindowSubclass(controlHwnd, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
			const auto minValue = 1.f, maxValue = 3.f;
			if (msg == WM_KEYDOWN && wParam == VK_DOWN && !GetWindowString(hWnd).starts_with(L"=")) {
				SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<float>(hWnd) + 0.1f));
				return 0;

			} else if (msg == WM_KEYDOWN && wParam == VK_UP && !GetWindowString(hWnd).starts_with(L"=")) {
				SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<float>(hWnd) - 0.1f));
				return 0;

			} else if (msg == WM_GETDLGCODE && wParam == VK_RETURN && GetWindowString(hWnd).starts_with(L"=")) {
				return DefSubclassProc(hWnd, msg, wParam, lParam) | DLGC_WANTALLKEYS;

			} else if (msg == WM_KEYDOWN && wParam == VK_RETURN) {
				if (const auto wstr = GetWindowString(hWnd); wstr.starts_with(L"=")) {
					if (float r; reinterpret_cast<FaceElementEditorDialog*>(dwRefData)->TryEvaluate(wstr, r))
						SetWindowNumber(hWnd, (std::min)(maxValue, (std::max)(minValue, r)));
					return 0;
				}
			}

			return DefSubclassProc(hWnd, msg, wParam, lParam);
		}, 1, reinterpret_cast<DWORD_PTR>(this));
	}

	for (const auto& controlHwnd : {
		m_controls->FontSizeEdit,
		}) {
		SetWindowSubclass(controlHwnd, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
			const auto minValue = 8.f, maxValue = 255.f;
			if (msg == WM_KEYDOWN && wParam == VK_DOWN && !GetWindowString(hWnd).starts_with(L"=")) {
				if (GetKeyState(VK_CONTROL) & 0x8000)
					SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<float>(hWnd) + 0.1f));
				else
					SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<float>(hWnd) + 1.f));
				return 0;

			} else if (msg == WM_KEYDOWN && wParam == VK_UP && !GetWindowString(hWnd).starts_with(L"=")) {
				if (GetKeyState(VK_CONTROL) & 0x8000)
					SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<float>(hWnd) - 0.1f));
				else
					SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<float>(hWnd) - 1.f));
				return 0;

			} else if (msg == WM_GETDLGCODE && wParam == VK_RETURN && GetWindowString(hWnd).starts_with(L"=")) {
				return DefSubclassProc(hWnd, msg, wParam, lParam) | DLGC_WANTALLKEYS;

			} else if (msg == WM_KEYDOWN && wParam == VK_RETURN) {
				if (const auto wstr = GetWindowString(hWnd); wstr.starts_with(L"=")) {
					if (float r; reinterpret_cast<FaceElementEditorDialog*>(dwRefData)->TryEvaluate(wstr, r))
						SetWindowNumber(hWnd, (std::min)(maxValue, (std::max)(minValue, r)));
					return 0;
				}
			}

			return DefSubclassProc(hWnd, msg, wParam, lParam);
		}, 1, reinterpret_cast<DWORD_PTR>(this));
	}

	for (const auto& controlHwnd : {
		m_controls->TransformationMatrixM11Edit,
		m_controls->TransformationMatrixM12Edit,
		m_controls->TransformationMatrixM21Edit,
		m_controls->TransformationMatrixM22Edit,
		}) {
		SetWindowSubclass(controlHwnd, [](HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData) -> LRESULT {
			const auto minValue = -100.f, maxValue = 100.f;
			if (msg == WM_KEYDOWN && wParam == VK_DOWN && !GetWindowString(hWnd).starts_with(L"=")) {
				if (GetKeyState(VK_CONTROL) & 0x8000)
					SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<float>(hWnd) + 0.01f));
				else
					SetWindowNumber(hWnd, (std::max)(minValue, GetWindowNumber<float>(hWnd) + 0.1f));
				return 0;

			} else if (msg == WM_KEYDOWN && wParam == VK_UP && !GetWindowString(hWnd).starts_with(L"=")) {
				if (GetKeyState(VK_CONTROL) & 0x8000)
					SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<float>(hWnd) - 0.01f));
				else
					SetWindowNumber(hWnd, (std::min)(maxValue, GetWindowNumber<float>(hWnd) - 0.1f));
				return 0;

			} else if (msg == WM_GETDLGCODE && wParam == VK_RETURN && GetWindowString(hWnd).starts_with(L"=")) {
				return DefSubclassProc(hWnd, msg, wParam, lParam) | DLGC_WANTALLKEYS;

			} else if (msg == WM_KEYDOWN && wParam == VK_RETURN) {
				if (const auto wstr = GetWindowString(hWnd); wstr.starts_with(L"=")) {
					if (float r; reinterpret_cast<FaceElementEditorDialog*>(dwRefData)->TryEvaluate(wstr, r))
						SetWindowNumber(hWnd, (std::min)(maxValue, (std::max)(minValue, r)));
					return 0;
				}
			}

			return DefSubclassProc(hWnd, msg, wParam, lParam);
		}, 1, reinterpret_cast<DWORD_PTR>(this));
	}

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

std::vector<std::pair<char32_t, char32_t>> App::FaceElementEditorDialog::ParseCustomRangeString() {
	const auto input = xivres::util::unicode::convert<std::u32string>(GetWindowString(m_controls->CustomRangeEdit));
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
				auto c1 = std::strtol(xivres::util::unicode::convert<std::string>(part.substr(2, sep - 2)).c_str(), nullptr, 16);
				auto part2 = part.substr(sep + 1);
				while (!part2.empty() && part2.front() < 128 && std::isspace(part2.front()))
					part2 = part2.substr(1);
				if (part2.starts_with(U"0x") || part2.starts_with(U"0X") || part2.starts_with(U"U+") || part2.starts_with(U"u+") || part2.starts_with(U"\\x") || part2.starts_with(U"\\X"))
					part2 = part2.substr(2);
				auto c2 = std::strtol(xivres::util::unicode::convert<std::string>(part2).c_str(), nullptr, 16);
				if (c1 < c2)
					ranges.emplace_back(c1, c2);
				else
					ranges.emplace_back(c2, c1);
			} else {
				const auto c = std::strtol(xivres::util::unicode::convert<std::string>(part.substr(2)).c_str(), nullptr, 16);
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

bool App::FaceElementEditorDialog::AddNewCodepointRange(char32_t c1, char32_t c2, const std::vector<char32_t>& charVec) {
	const auto newItem = std::make_pair(c1, c2);
	const auto it = std::ranges::lower_bound(m_element.WrapModifiers.Codepoints, newItem);
	if (it != m_element.WrapModifiers.Codepoints.end() && *it == newItem)
		return false;

	const auto newIndex = static_cast<int>(it - m_element.WrapModifiers.Codepoints.begin());
	m_element.WrapModifiers.Codepoints.insert(it, newItem);
	AddCodepointRangeToListBox(newIndex, c1, c2, charVec);
	return true;
}

void App::FaceElementEditorDialog::AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2, const std::vector<char32_t>& charVec) {
	const auto left = std::ranges::lower_bound(charVec, c1);
	const auto right = std::ranges::upper_bound(charVec, c2);
	const auto count = right - left;

	const auto block = std::lower_bound(xivres::util::unicode::blocks::all_blocks().begin(), xivres::util::unicode::blocks::all_blocks().end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
	if (block != xivres::util::unicode::blocks::all_blocks().end() && block->First == c1 && block->Last == c2) {
		if (c1 == c2) {
			ListBox_AddString(m_controls->CodepointsList, std::format(
				L"U+{:04X} {} [{}]",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::convert<std::wstring>(block->Name),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			).c_str());
		} else {
			ListBox_AddString(m_controls->CodepointsList, std::format(
				L"U+{:04X}~{:04X} {} ({}) {} ~ {}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				xivres::util::unicode::convert<std::wstring>(block->Name),
				count,
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			).c_str());
		}
	} else if (c1 == c2) {
		ListBox_AddString(m_controls->CodepointsList, std::format(
			L"U+{:04X} [{}]",
			static_cast<int>(c1),
			xivres::util::unicode::represent_codepoint<std::wstring>(c1)
		).c_str());
	} else {
		ListBox_AddString(m_controls->CodepointsList, std::format(
			L"U+{:04X}~{:04X} ({}) {} ~ {}",
			static_cast<uint32_t>(c1),
			static_cast<uint32_t>(c2),
			count,
			xivres::util::unicode::represent_codepoint<std::wstring>(c1),
			xivres::util::unicode::represent_codepoint<std::wstring>(c2)
		).c_str());
	}
}

void App::FaceElementEditorDialog::RefreshUnicodeBlockSearchResults() {
	const auto input = xivres::util::unicode::convert<std::string>(GetWindowString(m_controls->UnicodeBlockSearchNameEdit));
	const auto input32 = xivres::util::unicode::convert<std::u32string>(input);
	ListBox_ResetContent(m_controls->UnicodeBlockSearchResultList);

	const auto searchByChar = Button_GetCheck(m_controls->UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput);

	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
	for (const auto& block : xivres::util::unicode::blocks::all_blocks()) {
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
			xivres::util::unicode::convert<std::wstring>(nameView),
			right - left,
			xivres::util::unicode::represent_codepoint<std::wstring>(block.First),
			xivres::util::unicode::represent_codepoint<std::wstring>(block.Last)
		).c_str());
		ListBox_SetItemData(m_controls->UnicodeBlockSearchResultList, ListBox_GetCount(m_controls->UnicodeBlockSearchResultList) - 1, &block);
	}
}

void App::FaceElementEditorDialog::SetControlsEnabledOrDisabled() {
	switch (m_element.Renderer) {
		case App::Structs::RendererEnum::Empty:
			EnableWindow(m_controls->FontCombo, FALSE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, FALSE);
			EnableWindow(m_controls->FontStyleCombo, FALSE);
			EnableWindow(m_controls->FontStretchCombo, FALSE);
			EnableWindow(m_controls->EmptyAscentEdit, TRUE);
			EnableWindow(m_controls->EmptyLineHeightEdit, TRUE);
			EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
			EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
			EnableWindow(m_controls->AdjustmentBaselineShiftEdit, FALSE);
			EnableWindow(m_controls->AdjustmentLetterSpacingEdit, FALSE);
			EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, FALSE);
			EnableWindow(m_controls->AdjustmentGammaEdit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM11Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM12Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM21Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM22Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixHelp, FALSE);
			EnableWindow(m_controls->TransformationMatrixReset, FALSE);
			EnableWindow(m_controls->CustomRangeEdit, FALSE);
			EnableWindow(m_controls->CustomRangeAdd, FALSE);
			EnableWindow(m_controls->CodepointsList, FALSE);
			EnableWindow(m_controls->CodepointsDeleteButton, FALSE);
			EnableWindow(m_controls->CodepointsMergeModeCombo, FALSE);
			EnableWindow(m_controls->UnicodeBlockSearchNameEdit, FALSE);
			EnableWindow(m_controls->UnicodeBlockSearchResultList, FALSE);
			EnableWindow(m_controls->UnicodeBlockSearchAddAll, FALSE);
			EnableWindow(m_controls->UnicodeBlockSearchAdd, FALSE);
			break;

		case App::Structs::RendererEnum::PrerenderedGameInstallation:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, FALSE);
			EnableWindow(m_controls->FontStyleCombo, FALSE);
			EnableWindow(m_controls->FontStretchCombo, FALSE);
			EnableWindow(m_controls->EmptyAscentEdit, FALSE);
			EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
			EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
			EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
			EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
			EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
			EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
			EnableWindow(m_controls->AdjustmentGammaEdit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM11Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM12Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM21Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixM22Edit, FALSE);
			EnableWindow(m_controls->TransformationMatrixHelp, FALSE);
			EnableWindow(m_controls->TransformationMatrixReset, FALSE);
			EnableWindow(m_controls->CustomRangeEdit, TRUE);
			EnableWindow(m_controls->CustomRangeAdd, TRUE);
			EnableWindow(m_controls->CodepointsList, TRUE);
			EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
			EnableWindow(m_controls->CodepointsMergeModeCombo, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
			break;

		case App::Structs::RendererEnum::DirectWrite:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, TRUE);
			EnableWindow(m_controls->FontStyleCombo, TRUE);
			EnableWindow(m_controls->FontStretchCombo, TRUE);
			EnableWindow(m_controls->EmptyAscentEdit, FALSE);
			EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
			EnableWindow(m_controls->FreeTypeNoHintingCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoBitmapCheck, FALSE);
			EnableWindow(m_controls->FreeTypeForceAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeNoAutohintCheck, FALSE);
			EnableWindow(m_controls->FreeTypeRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteRenderModeCombo, TRUE);
			EnableWindow(m_controls->DirectWriteMeasureModeCombo, TRUE);
			EnableWindow(m_controls->DirectWriteGridFitModeCombo, TRUE);
			EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
			EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
			EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
			EnableWindow(m_controls->AdjustmentGammaEdit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM11Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM12Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM21Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM22Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixHelp, TRUE);
			EnableWindow(m_controls->TransformationMatrixReset, TRUE);
			EnableWindow(m_controls->CustomRangeEdit, TRUE);
			EnableWindow(m_controls->CustomRangeAdd, TRUE);
			EnableWindow(m_controls->CodepointsList, TRUE);
			EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
			EnableWindow(m_controls->CodepointsMergeModeCombo, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
			break;

		case App::Structs::RendererEnum::FreeType:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, TRUE);
			EnableWindow(m_controls->FontStyleCombo, TRUE);
			EnableWindow(m_controls->FontStretchCombo, TRUE);
			EnableWindow(m_controls->EmptyAscentEdit, FALSE);
			EnableWindow(m_controls->EmptyLineHeightEdit, FALSE);
			EnableWindow(m_controls->FreeTypeNoHintingCheck, TRUE);
			EnableWindow(m_controls->FreeTypeNoBitmapCheck, TRUE);
			EnableWindow(m_controls->FreeTypeForceAutohintCheck, TRUE);
			EnableWindow(m_controls->FreeTypeNoAutohintCheck, TRUE);
			EnableWindow(m_controls->FreeTypeRenderModeCombo, TRUE);
			EnableWindow(m_controls->DirectWriteRenderModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteMeasureModeCombo, FALSE);
			EnableWindow(m_controls->DirectWriteGridFitModeCombo, FALSE);
			EnableWindow(m_controls->AdjustmentBaselineShiftEdit, TRUE);
			EnableWindow(m_controls->AdjustmentLetterSpacingEdit, TRUE);
			EnableWindow(m_controls->AdjustmentHorizontalOffsetEdit, TRUE);
			EnableWindow(m_controls->AdjustmentGammaEdit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM11Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM12Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM21Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixM22Edit, TRUE);
			EnableWindow(m_controls->TransformationMatrixHelp, TRUE);
			EnableWindow(m_controls->TransformationMatrixReset, TRUE);
			EnableWindow(m_controls->CustomRangeEdit, TRUE);
			EnableWindow(m_controls->CustomRangeAdd, TRUE);
			EnableWindow(m_controls->CodepointsList, TRUE);
			EnableWindow(m_controls->CodepointsDeleteButton, TRUE);
			EnableWindow(m_controls->CodepointsMergeModeCombo, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchNameEdit, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchResultList, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAddAll, TRUE);
			EnableWindow(m_controls->UnicodeBlockSearchAdd, TRUE);
			break;
	}
}

void App::FaceElementEditorDialog::RepopulateFontCombobox() {
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

				auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
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

			auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
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

void App::FaceElementEditorDialog::RepopulateFontSubComboBox() {
	ComboBox_ResetContent(m_controls->FontWeightCombo);
	ComboBox_ResetContent(m_controls->FontStyleCombo);
	ComboBox_ResetContent(m_controls->FontStretchCombo);

	switch (m_element.Renderer) {
		case App::Structs::RendererEnum::PrerenderedGameInstallation:
		{
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

	SetWindowNumber(m_controls->FontSizeEdit, m_element.Size);
}

void App::FaceElementEditorDialog::OnBaseFontChanged() {
	m_bBaseFontChanged = true;
	m_element.OnFontCreateParametersChange();
	if (m_onFontChanged)
		m_onFontChanged();
}

void App::FaceElementEditorDialog::OnWrappedFontChanged() {
	m_bWrappedFontChanged = true;
	m_element.OnFontWrappingParametersChange();
	if (m_onFontChanged)
		m_onFontChanged();
}

INT_PTR App::FaceElementEditorDialog::DlgProc(UINT message, WPARAM wParam, LPARAM lParam) {
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
				case IDC_EDIT_FONT_SIZE: return FontSizeEdit_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_WEIGHT: return FontWeightCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_STYLE: return FontStyleCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_STRETCH: return FontStretchCombo_OnCommand(HIWORD(wParam));
				case IDC_EDIT_EMPTY_ASCENT: return EmptyAscentEdit_OnCommand(HIWORD(wParam));
				case IDC_EDIT_EMPTY_LINEHEIGHT: return EmptyLineHeightEdit_OnCommand(HIWORD(wParam));
				case IDC_CHECK_FREETYPE_NOHINTING:
				case IDC_CHECK_FREETYPE_NOBITMAP:
				case IDC_CHECK_FREETYPE_FORCEAUTOHINT:
				case IDC_CHECK_FREETYPE_NOAUTOHINT: return FreeTypeCheck_OnCommand(HIWORD(wParam), LOWORD(wParam), reinterpret_cast<HWND>(lParam));
				case IDC_COMBO_FREETYPE_RENDERMODE: return FreeTypeRenderModeCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_DIRECTWRITE_RENDERMODE: return DirectWriteRenderModeCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_DIRECTWRITE_MEASUREMODE: return DirectWriteMeasureModeCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_DIRECTWRITE_GRIDFITMODE: return DirectWriteGridFitModeCombo_OnCommand(HIWORD(wParam));
				case IDC_EDIT_ADJUSTMENT_BASELINESHIFT: return AdjustmentBaselineShiftEdit_OnCommand(HIWORD(wParam));
				case IDC_EDIT_ADJUSTMENT_LETTERSPACING: return AdjustmentLetterSpacingEdit_OnCommand(HIWORD(wParam));
				case IDC_EDIT_ADJUSTMENT_HORIZONTALOFFSET: return AdjustmentHorizontalOffsetEdit_OnCommand(HIWORD(wParam));
				case IDC_EDIT_ADJUSTMENT_GAMMA: return AdjustmentGammaEdit_OnCommand(HIWORD(wParam));
				case IDC_EDIT_TRANSFORMATIONMATRIX_M11: return TransformationMatrixEdit_OnCommand(0, HIWORD(wParam));
				case IDC_EDIT_TRANSFORMATIONMATRIX_M12: return TransformationMatrixEdit_OnCommand(1, HIWORD(wParam));
				case IDC_EDIT_TRANSFORMATIONMATRIX_M21: return TransformationMatrixEdit_OnCommand(2, HIWORD(wParam));
				case IDC_EDIT_TRANSFORMATIONMATRIX_M22: return TransformationMatrixEdit_OnCommand(3, HIWORD(wParam));
				case IDC_BUTTON_TRANSFORMATIONMATRIX_HELP: return TransformationMatrixHelpButton_OnCommand(3);
				case IDC_BUTTON_TRANSFORMATIONMATRIX_RESET: return TransformationMatrixResetButton_OnCommand(HIWORD(wParam));
				case IDC_EDIT_ADDCUSTOMRANGE_INPUT: return CustomRangeEdit_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_ADDCUSTOMRANGE_ADD: return CustomRangeAdd_OnCommand(HIWORD(wParam));
				case IDC_LIST_CODEPOINTS: return CodepointsList_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_CODEPOINTS_CLEAR: return CodepointsClearButton_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_CODEPOINTS_DELETE: return CodepointsDeleteButton_OnCommand(HIWORD(wParam));
				case IDC_COMBO_CODEPOINTS_MERGEMODE: return CodepointsMergeModeCombo_OnCommand(HIWORD(wParam));
				case IDC_EDIT_UNICODEBLOCKS_SEARCH: return UnicodeBlockSearchNameEdit_OnCommand(HIWORD(wParam));
				case IDC_CHECK_UNICODEBLOCKS_SHOWBLOCKSWITHANYOFCHARACTERSINPUT: return UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(HIWORD(wParam));
				case IDC_LIST_UNICODEBLOCKS_SEARCHRESULTS: return UnicodeBlockSearchResultList_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_UNICODEBLOCKS_ADDALL: return UnicodeBlockSearchAddAll_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_UNICODEBLOCKS_ADD: return UnicodeBlockSearchAdd_OnCommand(HIWORD(wParam));
				case IDC_BUTTON_EXPRESSION_HELP: return ExpressionHelpButton_OnCommand(HIWORD(wParam));
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
	}
	return 0;
}

INT_PTR __stdcall App::FaceElementEditorDialog::DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
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
