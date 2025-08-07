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
	HWND FontFeaturesList = GetDlgItem(Window, IDC_LIST_FONT_FEATURES);
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
	HWND TransformationMatrixHelp = GetDlgItem(Window, IDC_BUTTON_TRANSFORMATIONMATRIX_HELP);
	HWND TransformationMatrixReset = GetDlgItem(Window, IDC_BUTTON_TRANSFORMATIONMATRIX_RESET);
};

App::FaceElementEditorDialog::FaceElementEditorDialog(HWND hParentWnd, Structs::FaceElement& element, std::function<void()> onFontChanged)
	: m_element(element)
	, m_elementOriginal(element)
	, m_hParentWnd(hParentWnd)
	, m_onFontChanged(std::move(onFontChanged)) {
	auto res = FindResourceExW(g_hInstance, RT_DIALOG, MAKEINTRESOURCEW(IDD_FACEELEMENTEDITOR), g_langId);
	if (!res)
		res = FindResourceW(g_hInstance, MAKEINTRESOURCEW(IDD_FACEELEMENTEDITOR), RT_DIALOG);
	std::unique_ptr<std::remove_pointer_t<HGLOBAL>, decltype(&FreeResource)> hglob(LoadResource(g_hInstance, res), &FreeResource);
	CreateDialogIndirectParamW(
		g_hInstance,
		static_cast<DLGTEMPLATE*>(LockResource(hglob.get())),
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
		std::wstring errors(GetStringResource(IDS_ERROR_MATHEXPREVAL_BODY));
		for (size_t i = 0; i < parser.error_count(); i++) {
			const auto error = parser.get_error(i);
			errors += xivres::util::unicode::convert<std::wstring>(
				std::format(
					"\n* {:02} [{}] {}",
					error.token.position,
					to_str(error.mode),
					error.diagnostic));
		}

		if (!silent) {
			MessageBoxW(
				m_controls->Window,
				errors.c_str(),
				std::wstring(GetStringResource(IDS_ERROR_MATHEXPREVAL_TITLE)).c_str(),
				MB_OK | MB_ICONWARNING);
		}
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

	if (newValue == res)  // NOLINT(clang-diagnostic-float-equal)
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

	if (const auto v = GetComboboxSelData<Structs::RendererEnum>(m_controls->FontRendererCombo);
		v != m_element.Renderer) {
		m_element.Renderer = v;
		SetControlsEnabledOrDisabled();
		RepopulateFontCombobox();
		OnBaseFontChanged();
		RefreshUnicodeBlockSearchResults();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	IDWriteLocalizedStringsPtr names;
	if (FAILED(m_fontFamilies[ComboBox_GetCurSel(m_controls->FontCombo)]->GetFamilyNames(&names)))
		return -1;

	UINT32 index;
	if (BOOL exists; FAILED(names->FindLocaleName(L"en-us", &index, &exists)) || !exists) {
		if (FAILED(names->FindLocaleName(L"en", &index, &exists)) || !exists)
			index = 0;
	}

	UINT32 length;
	if (FAILED(names->GetStringLength(index, &length)))
		return -1;

	std::wstring name(length + 1, L'\0');
	if (FAILED(names->GetString(index, name.data(), length + 1)))
		return -1;

	name.resize(length);

	m_element.Lookup.Name = xivres::util::unicode::convert<std::string>(name);
	RepopulateFontSubComboBox();
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

	if (const auto v = GetComboboxSelData<DWRITE_FONT_WEIGHT>(m_controls->FontWeightCombo);
		v != m_element.Lookup.Weight) {
		m_element.Lookup.Weight = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontStyleCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<DWRITE_FONT_STYLE>(m_controls->FontStyleCombo); 
		v != m_element.Lookup.Style) {
		m_element.Lookup.Style = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontStretchCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<DWRITE_FONT_STRETCH>(m_controls->FontStretchCombo); 
		v != m_element.Lookup.Stretch) {
		m_element.Lookup.Stretch = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontFeaturesList_OnCommand(uint16_t notiCode) {
	if (notiCode != LBN_SELCHANGE)
		return 0;

	std::vector<int> selections;
	selections.resize(ListBox_GetSelCount(m_controls->FontFeaturesList));
	ListBox_GetSelItems(m_controls->FontFeaturesList, selections.size(), selections.data());

	m_element.Lookup.Features.clear();
	for (const auto& sel : selections) {
		const auto tag = static_cast<DWRITE_FONT_FEATURE_TAG>(ListBox_GetItemData(m_controls->FontFeaturesList, sel));
		m_element.Lookup.Features.insert(tag);
	}
	OnBaseFontChanged();
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

	if (const auto v = GetComboboxSelData<FT_Render_Mode>(m_controls->FreeTypeRenderModeCombo);
		v != m_element.RendererSpecific.FreeType.RenderMode) {
		m_element.RendererSpecific.FreeType.RenderMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteRenderModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<DWRITE_RENDERING_MODE>(m_controls->DirectWriteRenderModeCombo);
		v != m_element.RendererSpecific.DirectWrite.RenderMode) {
		m_element.RendererSpecific.DirectWrite.RenderMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteMeasureModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<DWRITE_MEASURING_MODE>(m_controls->DirectWriteMeasureModeCombo);
		v != m_element.RendererSpecific.DirectWrite.MeasureMode) {
		m_element.RendererSpecific.DirectWrite.MeasureMode = v;
		OnBaseFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::DirectWriteGridFitModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<DWRITE_GRID_FIT_MODE>(m_controls->DirectWriteGridFitModeCombo);
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
	ShellExecuteW(
		m_controls->Window,
		L"open",
		std::wstring(GetStringResource(IDS_URL_TRANSFORMATIONMATRIXWIKI)).c_str(),
		nullptr,
		nullptr,
		SW_SHOW);
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

	Edit_SetText(m_controls->CustomRangePreview, description.data());

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

	ListBox_GetSelItems(m_controls->CodepointsList, static_cast<int>(selItems.size()), selItems.data());
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

		ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), selItems.data());

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

	ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), selItems.data());

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
	ShellExecuteW(
		m_controls->Window,
		L"open",
		std::wstring(GetStringResource(IDS_URL_MATHEXPRHELP)).c_str(),
		nullptr,
		nullptr,
		SW_SHOW);
	return 0;
}

INT_PTR App::FaceElementEditorDialog::Dialog_OnInitDialog() {
	m_bOpened = true;
	SetControlsEnabledOrDisabled();
	RepopulateFontCombobox();
	RefreshUnicodeBlockSearchResults();
	SetComboboxContent<Structs::RendererEnum>(
		m_controls->FontRendererCombo,
		m_element.Renderer,
		{
			std::make_pair(Structs::RendererEnum::Empty, IDS_RENDERER_EMPTY),
			std::make_pair(Structs::RendererEnum::PrerenderedGameInstallation, IDS_RENDERER_PRERENDERED_GAME),
			std::make_pair(Structs::RendererEnum::DirectWrite, IDS_RENDERER_DIRECTWRITE),
			std::make_pair(Structs::RendererEnum::FreeType, IDS_RENDERER_FREETYPE),
		});
	ComboBox_SetText(m_controls->FontCombo, xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name).c_str());
	SetWindowNumber(m_controls->EmptyAscentEdit, m_element.RendererSpecific.Empty.Ascent);
	SetWindowNumber(m_controls->EmptyLineHeightEdit, m_element.RendererSpecific.Empty.LineHeight);
	Button_SetCheck(m_controls->FreeTypeNoHintingCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_HINTING) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeNoBitmapCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_BITMAP) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeForceAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_FORCE_AUTOHINT) ? TRUE : FALSE);
	Button_SetCheck(m_controls->FreeTypeNoAutohintCheck, (m_element.RendererSpecific.FreeType.LoadFlags & FT_LOAD_NO_AUTOHINT) ? TRUE : FALSE);
	
	SetComboboxContent<FT_Render_Mode>(
		m_controls->FreeTypeRenderModeCombo,
		m_element.RendererSpecific.FreeType.RenderMode,
		{
			std::make_pair(FT_RENDER_MODE_NORMAL, IDS_FREETYPE_RENDERMODE_NORMAL),
			std::make_pair(FT_RENDER_MODE_LIGHT, IDS_FREETYPE_RENDERMODE_LIGHT),
			std::make_pair(FT_RENDER_MODE_MONO, IDS_FREETYPE_RENDERMODE_MONO),
		});
	
	SetComboboxContent<DWRITE_RENDERING_MODE>(
		m_controls->DirectWriteRenderModeCombo,
		m_element.RendererSpecific.DirectWrite.RenderMode,
		{
			std::make_pair(DWRITE_RENDERING_MODE_DEFAULT, IDS_DIRECTWRITE_RENDERMODE_DEFAULT),
			std::make_pair(DWRITE_RENDERING_MODE_ALIASED, IDS_DIRECTWRITE_RENDERMODE_ALIASED),
			std::make_pair(DWRITE_RENDERING_MODE_GDI_CLASSIC, IDS_DIRECTWRITE_RENDERMODE_GDI_CLASSIC),
			std::make_pair(DWRITE_RENDERING_MODE_GDI_NATURAL, IDS_DIRECTWRITE_RENDERMODE_GDI_NATURAL),
			std::make_pair(DWRITE_RENDERING_MODE_NATURAL, IDS_DIRECTWRITE_RENDERMODE_NATURAL),
			std::make_pair(DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC, IDS_DIRECTWRITE_RENDERMODE_NATURAL_SYMMETRIC),
		});
	
	SetComboboxContent<DWRITE_MEASURING_MODE>(
		m_controls->DirectWriteMeasureModeCombo,
		m_element.RendererSpecific.DirectWrite.MeasureMode,
		{
			std::make_pair(DWRITE_MEASURING_MODE_NATURAL, IDS_DIRECTWRITE_MEASUREMODE_NATURAL),
			std::make_pair(DWRITE_MEASURING_MODE_GDI_CLASSIC, IDS_DIRECTWRITE_MEASUREMODE_GDI_CLASSIC),
			std::make_pair(DWRITE_MEASURING_MODE_GDI_NATURAL, IDS_DIRECTWRITE_MEASUREMODE_GDI_NATURAL),
		});
	
	SetComboboxContent<DWRITE_GRID_FIT_MODE>(
		m_controls->DirectWriteGridFitModeCombo,
		m_element.RendererSpecific.DirectWrite.GridFitMode,
		{
			std::make_pair(DWRITE_GRID_FIT_MODE_DEFAULT, IDS_DIRECTWRITE_GRIDFITMODE_DEFAULT),
			std::make_pair(DWRITE_GRID_FIT_MODE_DISABLED, IDS_DIRECTWRITE_GRIDFITMODE_DISABLED),
			std::make_pair(DWRITE_GRID_FIT_MODE_ENABLED, IDS_DIRECTWRITE_GRIDFITMODE_ENABLED),
		});

	SetWindowNumber(m_controls->AdjustmentBaselineShiftEdit, m_element.WrapModifiers.BaselineShift);
	SetWindowNumber(m_controls->AdjustmentLetterSpacingEdit, m_element.WrapModifiers.LetterSpacing);
	SetWindowNumber(m_controls->AdjustmentHorizontalOffsetEdit, m_element.WrapModifiers.HorizontalOffset);
	SetWindowNumber(m_controls->AdjustmentGammaEdit, m_element.Gamma);

	std::vector<char32_t> charVec(m_element.GetBaseFont()->all_codepoints().begin(), m_element.GetBaseFont()->all_codepoints().end());
	for (int i = 0, i_ = static_cast<int>(m_element.WrapModifiers.Codepoints.size()); i < i_; i++)
		AddCodepointRangeToListBox(i, m_element.WrapModifiers.Codepoints[i].first, m_element.WrapModifiers.Codepoints[i].second, charVec);

	SetComboboxContent<xivres::fontgen::codepoint_merge_mode>(
		m_controls->CodepointsMergeModeCombo,
		m_element.MergeMode,
		{
			std::make_pair(xivres::fontgen::codepoint_merge_mode::AddNew, IDS_CODEPOINTMERGEMODE_ADDNEW),
			std::make_pair(xivres::fontgen::codepoint_merge_mode::AddAll, IDS_CODEPOINTMERGEMODE_ADDALL),
			std::make_pair(xivres::fontgen::codepoint_merge_mode::Replace, IDS_CODEPOINTMERGEMODE_REPLACE),
		});

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
	std::vector<std::pair<char32_t, char32_t>> ranges;
	for (size_t i = 0, next; i < input.size(); i = next + 1) {
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
			ListBox_InsertString(m_controls->CodepointsList, index, std::format(
				L"U+{:04X} {} [{}]",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::convert<std::wstring>(block->Name),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			).c_str());
		} else {
			ListBox_InsertString(m_controls->CodepointsList, index, std::format(
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
		ListBox_InsertString(m_controls->CodepointsList, index, std::format(
			L"U+{:04X} [{}]",
			static_cast<int>(c1),
			xivres::util::unicode::represent_codepoint<std::wstring>(c1)
		).c_str());
	} else {
		ListBox_InsertString(m_controls->CodepointsList, index, std::format(
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
		case Structs::RendererEnum::Empty:
			EnableWindow(m_controls->FontCombo, FALSE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, FALSE);
			EnableWindow(m_controls->FontStyleCombo, FALSE);
			EnableWindow(m_controls->FontStretchCombo, FALSE);
			EnableWindow(m_controls->FontFeaturesList, FALSE);
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

		case Structs::RendererEnum::PrerenderedGameInstallation:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, FALSE);
			EnableWindow(m_controls->FontStyleCombo, FALSE);
			EnableWindow(m_controls->FontStretchCombo, FALSE);
			EnableWindow(m_controls->FontFeaturesList, FALSE);
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

		case Structs::RendererEnum::DirectWrite:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, TRUE);
			EnableWindow(m_controls->FontStyleCombo, TRUE);
			EnableWindow(m_controls->FontStretchCombo, TRUE);
			EnableWindow(m_controls->FontFeaturesList, TRUE);
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

		case Structs::RendererEnum::FreeType:
			EnableWindow(m_controls->FontCombo, TRUE);
			EnableWindow(m_controls->FontSizeEdit, TRUE);
			EnableWindow(m_controls->FontWeightCombo, TRUE);
			EnableWindow(m_controls->FontStyleCombo, TRUE);
			EnableWindow(m_controls->FontStretchCombo, TRUE);
			EnableWindow(m_controls->FontFeaturesList, FALSE);
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
		case Structs::RendererEnum::Empty:
			break;

		case Structs::RendererEnum::PrerenderedGameInstallation: {
			std::array<std::wstring, 8> ValidFonts{
				{
					L"AXIS",
					L"Jupiter",
					L"JupiterN",
					L"Meidinger",
					L"MiedingerMid",
					L"TrumpGothic",
					L"ChnAXIS",
					L"KrnAXIS",
				}
			};

			auto anySel = false;
			for (auto& name : ValidFonts) {
				ComboBox_AddString(m_controls->FontCombo, name.c_str());

				const auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
				if (lstrcmpiW(curNameLower.c_str(), name.c_str()) != 0)
					continue;

				anySel = true;
				ComboBox_SetCurSel(m_controls->FontCombo, ComboBox_GetCount(m_controls->FontCombo) - 1);
			}
			if (!anySel)
				ComboBox_SetCurSel(m_controls->FontCombo, 0);
			break;
		}

		case Structs::RendererEnum::DirectWrite:
		case Structs::RendererEnum::FreeType: {
			IDWriteFactory3Ptr factory;
			SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(&factory)));

			IDWriteFontCollectionPtr coll;
			SuccessOrThrow(factory->GetSystemFontCollection(&coll));

			m_fontFamilies.clear();
			std::vector<std::wstring> names;
			auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
			CharLowerBuffW(curNameLower.data(), static_cast<DWORD>(curNameLower.size()));
			for (uint32_t i = 0, i_ = coll->GetFontFamilyCount(); i < i_; i++) {
				IDWriteFontFamilyPtr family;
				IDWriteLocalizedStringsPtr strings;

				if (FAILED(coll->GetFontFamily(i, &family)))
					continue;

				if (FAILED(family->GetFamilyNames(&strings)))
					continue;

				uint32_t indexEng;
				if (BOOL exists; FAILED(strings->FindLocaleName(L"en-us", &indexEng, &exists)) || !exists) {
					if (FAILED(strings->FindLocaleName(L"en", &indexEng, &exists)) || !exists)
						indexEng = 0;
				}

				uint32_t index;
				if (BOOL exists; FAILED(strings->FindLocaleName(g_localeName.c_str(), &index, &exists)) || !exists)
					index = indexEng;

				uint32_t length;
				if (FAILED(strings->GetStringLength(index, &length)))
					continue;

				std::wstring res(length + 1, L'\0');
				if (FAILED(strings->GetString(index, res.data(), length + 1)))
					continue;
				res.resize(length);

				std::wstring resLower = res;
				CharLowerBuffW(resLower.data(), static_cast<DWORD>(resLower.size()));

				const auto insertAt = std::ranges::lower_bound(names, resLower) - names.begin();

				ComboBox_InsertString(m_controls->FontCombo, insertAt, res.c_str());
				m_fontFamilies.insert(m_fontFamilies.begin() + insertAt, std::move(family));

				if (curNameLower == resLower) {
					ComboBox_SetCurSel(m_controls->FontCombo, insertAt);
				} else if (indexEng != index) {
					if (FAILED(strings->GetStringLength(indexEng, &length)))
						continue;

					std::wstring res(length + 1, L'\0');
					if (FAILED(strings->GetString(indexEng, res.data(), length + 1)))
						continue;
					res.resize(length);

					CharLowerBuffW(res.data(), static_cast<DWORD>(res.size()));
					if (curNameLower == res)
						ComboBox_SetCurSel(m_controls->FontCombo, insertAt);
				}

				names.insert(names.begin() + insertAt, std::move(resLower));
			}

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
	ListBox_ResetContent(m_controls->FontFeaturesList);

	switch (m_element.Renderer) {
		case Structs::RendererEnum::PrerenderedGameInstallation: {
			ComboBox_SetCurSel(
				m_controls->FontWeightCombo,
				ComboBox_SetItemData(
					m_controls->FontWeightCombo,
					ComboBox_AddString(
						m_controls->FontWeightCombo,
						std::wstring(GetStringResource(IDS_FONTWEIGHT_400)).c_str()),
					m_element.Lookup.Weight = DWRITE_FONT_WEIGHT_NORMAL));
			ComboBox_SetCurSel(
				m_controls->FontStyleCombo,
				ComboBox_SetItemData(
					m_controls->FontStyleCombo,
					ComboBox_AddString(
						m_controls->FontStyleCombo,
						std::wstring(GetStringResource(IDS_FONTSTYLE_NORMAL)).c_str()),
					m_element.Lookup.Style = DWRITE_FONT_STYLE_NORMAL));
			ComboBox_SetCurSel(
				m_controls->FontStretchCombo, ComboBox_SetItemData(
					m_controls->FontStretchCombo,
					ComboBox_AddString(
						m_controls->FontStretchCombo,
						std::wstring(GetStringResource(IDS_FONTSTRETCH_NORMAL)).c_str()),
					m_element.Lookup.Stretch = DWRITE_FONT_STRETCH_NORMAL));
			break;
		}

		case Structs::RendererEnum::DirectWrite:
		case Structs::RendererEnum::FreeType: {
			IDWriteFactory3Ptr factory;
			SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(&factory)));

			IDWriteTextAnalyzerPtr analyzer;
			SuccessOrThrow(factory->CreateTextAnalyzer(&analyzer));

			IDWriteTextAnalyzer2Ptr analyzer2;
			SuccessOrThrow(analyzer->QueryInterface(&analyzer2));
			
			const auto curSel = (std::max)(0, (std::min)(static_cast<int>(m_fontFamilies.size() - 1), ComboBox_GetCurSel(m_controls->FontCombo)));
			const auto& family = m_fontFamilies[curSel];
			std::set<DWRITE_FONT_WEIGHT> weights;
			std::set<DWRITE_FONT_STYLE> styles;
			std::set<DWRITE_FONT_STRETCH> stretches;
			std::set<DWRITE_FONT_FEATURE_TAG> featureTags;

			for (uint32_t i = 0, i_ = family->GetFontCount(); i < i_; i++) {
				IDWriteFontPtr font;
				if (FAILED(family->GetFont(i, &font)))
					continue;

				if (font->GetSimulations() != DWRITE_FONT_SIMULATIONS_NONE
					&& m_element.Renderer == Structs::RendererEnum::FreeType)
					continue;

				weights.insert(font->GetWeight());
				styles.insert(font->GetStyle());
				stretches.insert(font->GetStretch());
				if (IDWriteFontFacePtr face; SUCCEEDED(font->CreateFontFace(&face))) {
					DWRITE_FONT_FEATURE_TAG tags[256];
					UINT32 tagCount{};
					if (SUCCEEDED(analyzer2->GetTypographicFeatures(
						face,
						{0, DWRITE_SCRIPT_SHAPES_DEFAULT},
						nullptr,
						_countof(tags),
						&tagCount,
						tags))) {
						featureTags.insert(tags, tags + tagCount);
					}
				}
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

			std::wstring text;
			for (const auto v : weights) {
				switch (v) {
					case 100:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_100));
						break;
					case 200:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_200));
						break;
					case 300:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_300));
						break;
					case 350:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_350));
						break;
					case 400:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_400));
						break;
					case 500:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_500));
						break;
					case 600:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_600));
						break;
					case 700:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_700));
						break;
					case 800:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_800));
						break;
					case 900:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_900));
						break;
					case 950:
						text = std::wstring(GetStringResource(IDS_FONTWEIGHT_950));
						break;
					default:
						text = std::format(L"{}", static_cast<int>(v)).c_str();
						break;
				}

				const auto index = ComboBox_AddString(m_controls->FontWeightCombo, text.c_str());
				ComboBox_SetItemData(m_controls->FontWeightCombo, index, static_cast<uint32_t>(v));

				if (v == closestWeight) {
					ComboBox_SetCurSel(m_controls->FontWeightCombo, ComboBox_GetCount(m_controls->FontWeightCombo) - 1);
					m_element.Lookup.Weight = v;
				}
			}

			for (const auto v : styles) {
				switch (v) {
					case DWRITE_FONT_STYLE_NORMAL:
						text = std::wstring(GetStringResource(IDS_FONTSTYLE_NORMAL));
						break;
					case DWRITE_FONT_STYLE_OBLIQUE:
						text = std::wstring(GetStringResource(IDS_FONTSTYLE_OBLIQUE));
						break;
					case DWRITE_FONT_STYLE_ITALIC:
						text = std::wstring(GetStringResource(IDS_FONTSTYLE_ITALIC));
						break;
					default:
						continue;
				}

				const auto index = ComboBox_AddString(m_controls->FontStyleCombo, text.c_str());
				ComboBox_SetItemData(m_controls->FontStyleCombo, index, static_cast<uint32_t>(v));

				if (v == closestStyle) {
					ComboBox_SetCurSel(m_controls->FontStyleCombo, ComboBox_GetCount(m_controls->FontStyleCombo) - 1);
					m_element.Lookup.Style = v;
				}
			}

			for (const auto v : stretches) {
				switch (v) {
					case DWRITE_FONT_STRETCH_ULTRA_CONDENSED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_ULTRA_CONDENSED));
						break;
					case DWRITE_FONT_STRETCH_EXTRA_CONDENSED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_EXTRA_CONDENSED));
						break;
					case DWRITE_FONT_STRETCH_CONDENSED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_CONDENSED));
						break;
					case DWRITE_FONT_STRETCH_SEMI_CONDENSED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_SEMI_CONDENSED));
						break;
					case DWRITE_FONT_STRETCH_NORMAL:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_NORMAL));
						break;
					case DWRITE_FONT_STRETCH_SEMI_EXPANDED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_SEMI_EXPANDED));
						break;
					case DWRITE_FONT_STRETCH_EXPANDED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_EXPANDED));
						break;
					case DWRITE_FONT_STRETCH_EXTRA_EXPANDED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_EXTRA_EXPANDED));
						break;
					case DWRITE_FONT_STRETCH_ULTRA_EXPANDED:
						text = std::wstring(GetStringResource(IDS_FONTSTRETCH_ULTRA_EXPANDED));
						break;
					default:
						continue;
				}

				const auto index = ComboBox_AddString(m_controls->FontStretchCombo, text.c_str());
				ComboBox_SetItemData(m_controls->FontStretchCombo, index, static_cast<uint32_t>(v));

				if (v == closestStretch) {
					ComboBox_SetCurSel(m_controls->FontStretchCombo, ComboBox_GetCount(m_controls->FontStretchCombo) - 1);
					m_element.Lookup.Stretch = v;
				}
			}

			for (const auto v : featureTags) {
				const auto index = ListBox_AddString(
					m_controls->FontFeaturesList,
					std::format(
						L"{}: {}",
						xivres::util::unicode::convert<std::wstring>(std::string_view(reinterpret_cast<const char*>(&v), 4)),
						GetOpenTypeFeatureName(v)).c_str());
				ListBox_SetItemData(m_controls->FontFeaturesList, index, static_cast<uint32_t>(v));
				if (m_element.Lookup.Features.contains(v))
					ListBox_SetSel(m_controls->FontFeaturesList, TRUE, index);
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
		case WM_COMMAND: {
			switch (LOWORD(wParam)) {
				case IDOK: return OkButton_OnCommand(HIWORD(wParam));
				case IDCANCEL: return CancelButton_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_RENDERER: return FontRendererCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT: return FontCombo_OnCommand(HIWORD(wParam));
				case IDC_EDIT_FONT_SIZE: return FontSizeEdit_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_WEIGHT: return FontWeightCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_STYLE: return FontStyleCombo_OnCommand(HIWORD(wParam));
				case IDC_COMBO_FONT_STRETCH: return FontStretchCombo_OnCommand(HIWORD(wParam));
				case IDC_LIST_FONT_FEATURES: return FontFeaturesList_OnCommand(HIWORD(wParam));
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
		case WM_CLOSE: {
			EndDialog(m_controls->Window, 0);
			m_bOpened = false;
			return 0;
		}
		case WM_DESTROY: {
			m_bOpened = false;
			return 0;
		}
	}
	return 0;
}

INT_PTR __stdcall App::FaceElementEditorDialog::DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
	if (message == WM_INITDIALOG) {
		auto& params = *reinterpret_cast<FaceElementEditorDialog*>(lParam);
		params.m_controls = new ControlStruct{hwnd};
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&params));
		return params.DlgProc(message, wParam, lParam);
	} else {
		return reinterpret_cast<FaceElementEditorDialog*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA))->DlgProc(message, wParam, lParam);
	}
	return 0;
}
