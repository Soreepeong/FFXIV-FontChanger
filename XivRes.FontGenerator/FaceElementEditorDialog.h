#pragma once

#include "Structs.h"

namespace App {
	class FaceElementEditorDialog {
		struct ControlStruct;

		Structs::FaceElement& m_element;
		Structs::FaceElement m_elementOriginal;
		HWND m_hParentWnd;
		bool m_bOpened = false;
		std::function<void()> m_onFontChanged;
		bool m_bBaseFontChanged = false;
		bool m_bWrappedFontChanged = false;

		std::vector<IDWriteFontFamilyPtr> m_fontFamilies;

		ControlStruct* m_controls = nullptr;

	public:
		FaceElementEditorDialog(HWND hParentWnd, Structs::FaceElement& element, std::function<void()> onFontChanged);
		FaceElementEditorDialog(FaceElementEditorDialog&&) = delete;
		FaceElementEditorDialog(const FaceElementEditorDialog&) = delete;
		FaceElementEditorDialog operator =(FaceElementEditorDialog&&) = delete;
		FaceElementEditorDialog operator =(const FaceElementEditorDialog&) = delete;

		~FaceElementEditorDialog();

		bool IsOpened() const;

		void Activate() const;

		bool ConsumeDialogMessage(MSG& msg);

	private:
		template<typename T>
		bool TryEvaluate(const std::wstring& wstr, T& res, bool silent = false);

		template<typename T>
		bool TryGetOrEvaluateValueInto(HWND hwnd, T& res, const T& originalValue);

		INT_PTR OkButton_OnCommand(uint16_t notiCode);

		INT_PTR CancelButton_OnCommand(uint16_t notiCode);

		INT_PTR FontRendererCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontSizeEdit_OnCommand(uint16_t notiCode);

		INT_PTR FontWeightCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontStyleCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontStretchCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontFeaturesList_OnCommand(uint16_t notiCode);

		INT_PTR EmptyAscentEdit_OnCommand(uint16_t notiCode);

		INT_PTR EmptyLineHeightEdit_OnCommand(uint16_t notiCode);

		INT_PTR FreeTypeCheck_OnCommand(uint16_t notiCode, uint16_t id, HWND hWnd);

		INT_PTR FreeTypeRenderModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteRenderModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteMeasureModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteGridFitModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentBaselineShiftEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentLetterSpacingEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentHorizontalOffsetEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentGammaEdit_OnCommand(uint16_t notiCode);

		INT_PTR TransformationMatrixEdit_OnCommand(int index, uint16_t notiCode);

		INT_PTR TransformationMatrixHelpButton_OnCommand(uint16_t notiCode);

		INT_PTR TransformationMatrixResetButton_OnCommand(uint16_t notiCode);

		INT_PTR CustomRangeEdit_OnCommand(uint16_t notiCode);

		INT_PTR CustomRangeAdd_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsList_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsClearButton_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsDeleteButton_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsMergeModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchNameEdit_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchResultList_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchAddAll_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchAdd_OnCommand(uint16_t notiCode);

		INT_PTR ExpressionHelpButton_OnCommand(uint16_t notiCode);

		INT_PTR Dialog_OnInitDialog();

		std::vector<std::pair<char32_t, char32_t>> ParseCustomRangeString();
		bool AddNewCodepointRange(char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void RefreshUnicodeBlockSearchResults();

		void SetControlsEnabledOrDisabled();
		void RepopulateFontCombobox();
		void RepopulateFontSubComboBox();

		void OnBaseFontChanged();
		void OnWrappedFontChanged();

		INT_PTR DlgProc(UINT message, WPARAM wParam, LPARAM lParam);
		static INT_PTR __stdcall DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
	};
}
