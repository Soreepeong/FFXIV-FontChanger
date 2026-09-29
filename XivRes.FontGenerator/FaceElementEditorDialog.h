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

		struct FeatureRow {
			DWRITE_FONT_FEATURE_TAG Tag;

			// Number of alternates that the feature can choose from; the value of the feature picks one if above 1.
			uint32_t Alternates;
		};

		// Features that the selected font supports.
		std::vector<FeatureRow> m_features;
		bool m_bPopulatingFeatures = false;
		bool m_bSettingFeatureValueEdit = false;

		// Language tags of the items of the language combobox; empty for unspecified.
		std::vector<std::string> m_languageTags;

		struct VariationAxis {
			uint32_t Tag;
			std::wstring Name;
			float Minimum;
			float Maximum;
			float InstanceValue;
		};

		// Axes of the selected font, if it is a variable font.
		std::vector<VariationAxis> m_variationAxes;
		bool m_bSettingVariationValueEdit = false;

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

		HWND m_hWnd{};

		template<typename T>
		friend INT_PTR __stdcall ::DlgProcStaticImpl(HWND, UINT, WPARAM, LPARAM);

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

		INT_PTR FontFeaturesList_OnItemChanged(const NMLISTVIEW& nmlv);

		INT_PTR FontFeatureValueEdit_OnCommand(uint16_t notiCode);

		INT_PTR FontLanguageCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontVariationsList_OnItemChanged(const NMLISTVIEW& nmlv);

		INT_PTR FontVariationValueEdit_OnCommand(uint16_t notiCode);

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

		INT_PTR CustomRangeSubtract_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsList_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsClearButton_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsDeleteButton_OnCommand(uint16_t notiCode);

		INT_PTR CodepointsMergeModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchNameEdit_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchResultList_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchAddAll_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchAdd_OnCommand(uint16_t notiCode);

		INT_PTR UnicodeBlockSearchSubtract_OnCommand(uint16_t notiCode);

		INT_PTR ExpressionHelpButton_OnCommand(uint16_t notiCode);

		INT_PTR Dialog_OnInitDialog();

		void ShowPage(int index);

		std::vector<std::pair<char32_t, char32_t>> ParseCustomRangeString();
		bool AddNewCodepointRange(char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void RemoveCodepointRanges(const std::vector<std::pair<char32_t, char32_t>>& ranges);
		void RefreshUnicodeBlockSearchResults();

		void SetControlsEnabledOrDisabled();
		void RepopulateFontCombobox();
		void RepopulateFontSubComboBox();
		void UpdateFontFeaturesListItem(int index);
		int GetSelectedFontFeature() const;
		void RefreshFontFeatureValueEdit();
		void RepopulateFontLanguageCombobox();
		void RepopulateFontVariationsList();
		void UpdateFontVariationsListItem(int index);
		int GetSelectedFontVariationAxis() const;
		void RefreshFontVariationValueEdit();

		void OnBaseFontChanged();
		void OnWrappedFontChanged();

		INT_PTR DlgProc(UINT message, WPARAM wParam, LPARAM lParam);
		static INT_PTR __stdcall DlgProcStatic(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

		// Procedure of the pages of the tab control, which forwards notifications from their controls to the dialog.
		static INT_PTR __stdcall PageDlgProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
	};
}
