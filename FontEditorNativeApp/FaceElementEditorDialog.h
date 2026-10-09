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

		// Called with whether the element is to be left out of the preview of the face.
		std::function<void(bool)> m_onDeactivatedChange;
		bool m_bDeactivated = false;

		bool m_bBaseFontChanged = false;
		bool m_bWrappedFontChanged = false;

		std::vector<IDWriteFontFamilyPtr> m_fontFamilies;

		struct FeatureRow {
			DWRITE_FONT_FEATURE_TAG Tag;

			// Number of alternates that the feature can choose from; the value of the feature picks one if above 1.
			uint32_t Alternates;

			// Whether shaping applies the feature when it is left at the default.
			bool DefaultOn;
		};

		// Features that the selected font supports.
		std::vector<FeatureRow> m_features;
		bool m_bSettingFeatureValueCombo = false;

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

		// Set while the Glyph merging page is filled in from the element, so that the changes are not taken as edits.
		bool m_bRefreshingGlyphMerging = false;

		enum class MonospacingMode : uint8_t {
			Off,
			Fixed,
			AtLeast,
			AtMost,
			Between,
		};

		// Kept apart from the element, which cannot tell Between with equal limits from Fixed.
		MonospacingMode m_monospacingMode = MonospacingMode::Off;
		bool m_bRefreshingMonospacing = false;

		ControlStruct* m_controls = nullptr;

	public:
		FaceElementEditorDialog(HWND hParentWnd, Structs::FaceElement& element, std::function<void()> onFontChanged, std::function<void(bool)> onDeactivatedChange);
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

		// Keeps the changes so far, which Cancel then no longer reverts.
		INT_PTR ApplyButton_OnCommand(uint16_t notiCode);

		INT_PTR DeactivateCheck_OnCommand(uint16_t notiCode);

		// Activates the element again if the dialog deactivated it, as the dialog closes.
		void Reactivate();

		INT_PTR FontRendererCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontSizeEdit_OnCommand(uint16_t notiCode);

		INT_PTR FontWeightCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontStyleCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontStretchCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontAllowSynthesisCheck_OnCommand(uint16_t notiCode);

		INT_PTR FontFeaturesList_OnItemChanged(const NMLISTVIEW& nmlv);

		INT_PTR FontFeaturesList_OnKeyDown(const NMLVKEYDOWN& nmkd);

		INT_PTR FontFeaturesList_OnDblClick(const NMITEMACTIVATE& nmia);

		INT_PTR FontFeaturesList_OnCustomDraw(const NMLVCUSTOMDRAW& nmcd);

		// screenPos is (-1, -1) if the menu is opened from the keyboard.
		INT_PTR FontFeaturesList_OnContextMenu(POINT screenPos);

		INT_PTR FontFeatureValueCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontLanguageCombo_OnCommand(uint16_t notiCode);

		INT_PTR FontVariationsList_OnItemChanged(const NMLISTVIEW& nmlv);

		INT_PTR FontVariationValueEdit_OnCommand(uint16_t notiCode);

		INT_PTR EmptyAscentEdit_OnCommand(uint16_t notiCode);

		INT_PTR EmptyLineHeightEdit_OnCommand(uint16_t notiCode);

		INT_PTR FreeTypeHinting_OnCommand(uint16_t notiCode);

		INT_PTR FreeTypeRenderModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteRenderModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteMeasureModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR DirectWriteGridFitModeCombo_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentBaselineShiftEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentLetterSpacingEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentHorizontalOffsetEdit_OnCommand(uint16_t notiCode);

		INT_PTR AdjustmentGammaEdit_OnCommand(uint16_t notiCode);

		// index: 0 for scale x, 1 for scale y, 2 for skew, and 3 for rotation.
		INT_PTR TransformEdit_OnCommand(int index, uint16_t notiCode);

		// Returns the transformation of the element as components, decomposing a matrix of an earlier version.
		[[nodiscard]] Structs::TransformStruct GetEditableTransform() const;

		void RefreshTransformEdits();

		// Controls of the SVG/PNG glyphs group of the Font page.
		INT_PTR GlyphImages_OnCommand(uint16_t id, uint16_t notiCode);

		INT_PTR GlyphImagesBrowseButton_OnCommand(uint16_t notiCode);

		INT_PTR GlyphImagesEmbedButton_OnCommand(uint16_t notiCode);

		void InitializeGlyphImagesGroup();
		void RefreshGlyphImagesGroup();
		void SetGlyphImagesControlsEnabled(bool enabled);

		// Set while the SVG/PNG glyphs group is filled in from the element.
		bool m_bRefreshingGlyphImages = false;

		// Controls of the Monospacing group of the Font page.
		INT_PTR Monospacing_OnCommand(uint16_t id, uint16_t notiCode);

		void InitializeMonospacingControls();

		// Sets the limits of the element from the mode and the width edits.
		void ApplyMonospacingWidths();

		void SetMonospacingControlsEnabled();

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

		// Text placement controls of the Glyph merging page.
		INT_PTR GlyphMergingPlacement_OnCommand(uint16_t id, uint16_t notiCode);

		INT_PTR GlyphMergingMappingsList_OnItemChanged(const NMLISTVIEW& nmlv);

		INT_PTR GlyphMergingAddButton_OnCommand(uint16_t notiCode);

		INT_PTR GlyphMergingDeleteButton_OnCommand(uint16_t notiCode);

		// Controls that edit the selected mapping.
		INT_PTR GlyphMergingMappingEditor_OnCommand(uint16_t id, uint16_t notiCode);

		INT_PTR GlyphMergingImportSvgButton_OnCommand(uint16_t notiCode);

		INT_PTR GlyphMergingPresetCheck_OnCommand(size_t presetIndex, uint16_t notiCode);

		INT_PTR Dialog_OnInitDialog();

		void ShowPage(int index);

		std::vector<std::pair<char32_t, char32_t>> ParseCustomRangeString();

		// Parses ranges such as "U+E08F-E094, U+E0AF", or the characters themselves.
		static std::vector<std::pair<char32_t, char32_t>> ParseCodepointRanges(std::wstring_view text);

		void InitializeGlyphMergingPage();
		void RefreshGlyphMergingPlacement();
		void RefreshGlyphMergingMappingsList(int selectIndex);
		void UpdateGlyphMergingMappingsListItem(int index);
		void RefreshGlyphMergingMappingEditor();
		void RefreshGlyphMergingPresets();
		void SetGlyphMergingControlsEnabled(bool enabled);
		[[nodiscard]] int GetSelectedGlyphMergingMapping() const;
		bool AddNewCodepointRange(char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2, const std::vector<char32_t>& charVec);
		void RemoveCodepointRanges(const std::vector<std::pair<char32_t, char32_t>>& ranges);
		void RefreshUnicodeBlockSearchResults();

		void SetControlsEnabledOrDisabled();
		void RepopulateFontCombobox();
		// Lists the weights, styles, and widths of the selected family, keeping the requested ones if they are listed.
		// snapToReal: whether to pick the closest real ones instead, as when the family changes.
		void RepopulateFontSubComboBox(bool snapToReal = false);
		void RefreshFontFeatureDefaults();
		void UpdateFontFeaturesListItem(int index);
		int GetSelectedFontFeature() const;

		// Values that a feature can be set to, in the order of the choices: std::nullopt for the default, then 1 (on),
		// 0 (off), and the alternates from 2.
		[[nodiscard]] std::vector<std::optional<uint32_t>> GetFontFeatureChoices(int index) const;
		[[nodiscard]] std::wstring GetFontFeatureChoiceText(int index, std::optional<uint32_t> value) const;
		[[nodiscard]] std::optional<uint32_t> GetFontFeatureValue(int index) const;
		void SetFontFeatureValue(int index, std::optional<uint32_t> value, bool refreshCombo = true);
		void RefreshFontFeatureValueCombo();
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
