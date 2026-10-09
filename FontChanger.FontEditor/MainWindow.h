#pragma once

#include "BaseWindow.h"
#include "FontChanger.Presets/ElementFonts.h"
#include "MainWindow.Internal.h"

namespace App {
	class FaceElementEditorDialog;
	class ProgressDialog;

	// The font data of the faces of a font set, and the textures of their glyphs.
	struct CompiledFontSet {
		std::vector<std::shared_ptr<xivres::fontdata::stream>> Fdts;
		std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>> Mipmaps;
	};

	class FontEditorWindow : public BaseWindow {
		static constexpr auto ClassName = L"FontEditorWindowClass";

		enum : uint8_t {
			Id_None,
			Id_FaceListBox,
			Id_FaceElementListView,
			Id_Edit,
			Id_Last_,
		};

		enum class CompressionMode : uint8_t {
			CompressWhilePacking,
			CompressAfterPacking,
			DoNotCompress,
		};

		enum class VerticalSplitter : uint8_t {
			None,
			ListEdit,
			EditPreview,
		};

		const std::vector<std::wstring> m_args;

		bool m_bChanged = false;
		std::filesystem::path m_currentPath;
		Structs::MultiFontSet m_multiFontSet;
		Structs::Face* m_pActiveFace = nullptr;

		// Elements left out of the preview, such as for comparing a face without them; not saved.
		std::set<const Structs::FaceElement*> m_deactivatedElements;

		// The merged font of the active face without them, and what it was made of.
		struct {
			std::vector<const void*> Key;
			std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> Font;
		} m_previewFont;

		std::shared_ptr<xivres::texture::memory_mipmap_stream> m_pMipmap;
		std::map<Structs::FaceElement*, std::unique_ptr<FaceElementEditorDialog>> m_editors;
		bool m_bNeedRedraw = false;
		bool m_bWordWrap = false;
		bool m_bKerning = false;
		bool m_bShowLineMetrics = true;
		xivres::font_type m_hotReloadFontType = xivres::font_type::undefined;

		int m_scaledListViewHeight = 0;
		int m_scaledEditHeight = 0;
		int m_splitterThicknessPx = 0;
		int m_splitterListEditTop = 0;
		int m_splitterListEditBottom = 0;
		int m_splitterEditPreviewTop = 0;
		int m_splitterEditPreviewBottom = 0;

		HWND m_hWnd{};
		HACCEL m_hAccelerator{};
		HFONT m_hUiFont{};

		HWND m_hFacesListBox{};
		HWND m_hFaceElementsListView{};
		HWND m_hEdit{};
		HMENU m_hFaceElementContextMenu{};

		int m_nDrawLeft{};
		int m_nDrawTop{};
		int m_nZoom = PreviewZoomOne;

		int m_nPreviewScrollX = 0;
		int m_nPreviewScrollY = 0;
		int m_nPreviewContentWidth = 0;
		int m_nPreviewContentHeight = 0;
		bool m_nPreviewMayScroll = false;

		bool m_bIsReorderingFaceElementList = false;
		VerticalSplitter m_activeSplitter = VerticalSplitter::None;

		struct {
			bool Active{};
			POINT StartPosition{};
			POINT DragRelativePosition{};
		} m_previewPan;

	public:
		FontEditorWindow(std::vector<std::wstring> args);
		FontEditorWindow(FontEditorWindow&&) = delete;
		FontEditorWindow(const FontEditorWindow&) = delete;
		FontEditorWindow operator =(FontEditorWindow&&) = delete;
		FontEditorWindow operator =(const FontEditorWindow&) = delete;

		~FontEditorWindow() override;

		bool ConsumeDialogMessage(MSG& msg) override;

		bool ConsumeAccelerator(MSG& msg) override;

	private:
		LRESULT Window_OnCreate(HWND hwnd);
		LRESULT Window_OnSize();
		LRESULT Window_OnPaint();
		LRESULT Window_OnInitMenuPopup(HMENU hMenu, int index, bool isWindowMenu);
		LRESULT Window_OnMouseMove(uint16_t states, int16_t x, int16_t y);
		LRESULT Window_OnMouseLButtonDown(uint16_t states, int16_t x, int16_t y);
		LRESULT Window_OnMouseLButtonUp(uint16_t states, int16_t x, int16_t y);
		LRESULT Window_OnMouseWheel(int16_t delta, int16_t x, int16_t y);
		LRESULT Window_OnSetCursor(HWND hContainer, int hittest, int wm);
		LRESULT Window_OnDestroy();
		void Window_Redraw();

		LRESULT Menu_File_New(xivres::font_type fontType);
		LRESULT Menu_File_Open();
		LRESULT Menu_File_Save();
		LRESULT Menu_File_SaveAs(bool changeCurrentFile);
		LRESULT Menu_File_OpenRecent(size_t index);
		LRESULT Menu_File_ClearRecent();
		void PopulateRecentFilesMenu(HMENU hMenu);
		LRESULT Menu_File_Language(const char* language);
		LRESULT Menu_File_GameInstallationManager();
		LRESULT Menu_File_Exit();

		LRESULT Menu_Edit_Add();

		// Adds the elements that draw the faces of the game font family of the active face with a font that the user
		// chooses, to each face of the family in its font set; see FaceFromFont::MakeElements.
		LRESULT Menu_Edit_AddFromFont();
		LRESULT Menu_Edit_Cut();
		LRESULT Menu_Edit_Copy();
		LRESULT Menu_Edit_Paste();
		LRESULT Menu_Edit_Delete();
		LRESULT Menu_Edit_SelectAll();
		LRESULT Menu_Edit_Details();
		LRESULT Menu_Edit_ChangeParams(int baselineShift, int horizontalOffset, int letterSpacing, float fontSize);
		LRESULT Menu_Edit_ToggleMergeMode();
		LRESULT Menu_Edit_SetVerticalAlignment(FontChanger::FixedSizeFont::vertical_alignment alignment);
		LRESULT Menu_Edit_MoveUpOrDown(int direction);
		LRESULT Menu_Edit_CreateEmptyCopyFromSelection();

		LRESULT Menu_View_NextOrPrevFont(int direction);
		LRESULT Menu_View_WordWrap();
		LRESULT Menu_View_Kerning();
		LRESULT Menu_View_ShowLineMetrics();
		LRESULT Menu_View_Zoom(int zoom);

		LRESULT Menu_Export_Preview();
		LRESULT Menu_Export_Raw();
		LRESULT Menu_Export_TTMP(CompressionMode compressionMode);
		LRESULT Menu_Export_ToggleMapping(const ExportMapping& mapping);
		LRESULT Menu_Export_Glyphs(bool withAdjustments);
		LRESULT Menu_Export_FaceGlyphs();

		LRESULT Menu_HotReload_Reload(bool restore);
		LRESULT Menu_HotReload_Font(xivres::font_type mode);

		LRESULT Edit_OnCommand(uint16_t commandId);

		LRESULT FaceListBox_OnCommand(uint16_t commandId);

		// screenPos is (-1, -1) if the menu is opened from the keyboard.
		LRESULT FaceListBox_OnContextMenu(POINT screenPos);

		// Scales the elements of the face to a size that the user enters.
		void FaceListBox_ScaleToSize(Structs::Face& face);

		// Replaces the elements of the faces of the same game font family in the font set with copies of the elements of
		// the face, scaled from its size to theirs. Sizes of faces are those of their first elements, or for faces without
		// elements, those of the game fonts by their names.
		void FaceListBox_ReplaceOtherSizes(Structs::FontSet& fontSet, const Structs::Face& face);

		// Closes the editors of the elements of the face.
		void CloseEditors(const Structs::Face& face);

		LRESULT FaceElementsListView_OnBeginDrag(NM_LISTVIEW& nmlv);
		bool FaceElementsListView_OnDragProcessMouseUp(int16_t x, int16_t y);
		bool FaceElementsListView_OnDragProcessMouseMove(int16_t x, int16_t y);
		bool FaceElementsListView_DragProcessDragging(int16_t x, int16_t y);
		LRESULT FaceElementsListView_OnDblClick(NMITEMACTIVATE& nmia);
		LRESULT FaceElementsListView_OnRightClick(NMITEMACTIVATE& nmia);
		LRESULT FaceElementsListView_Clone();
		LRESULT FaceElementsListView_ShowNegativeBearingCodepoints();

		[[nodiscard]] double GetZoom() const noexcept;

		VerticalSplitter HitTestSplitter(int16_t x, int16_t y) const;
		void UpdateSplitterDragPosition(int16_t y);
		void EndSplitterDrag();

		void OpenFile(std::filesystem::path path);
		void SetCurrentMultiFontSet(Structs::MultiFontSet multiFontSet, std::filesystem::path path);
		[[nodiscard]] std::wstring GetCurrentFileName() const;

		// Finds the font set that has the face.
		[[nodiscard]] Structs::FontSet* FindFontSet(const Structs::Face& face) const;

		// Gets the indices of the selected elements of the active face, in ascending order.
		[[nodiscard]] std::vector<int> GetSelectedElementIndices() const;

		// Marks the changes and draws again after the elements of the active face changed.
		void OnActiveFaceElementsChanged();

		// Takes the folder of the current file as the one that relative paths in the configuration are resolved against.
		void UpdateProjectDirectory();

		// Watches the folders of glyph files, and draws again with the changed files.
		void StartWatchingGlyphFolders();
		void StopWatchingGlyphFolders();
		[[nodiscard]] static UINT GetGlyphFoldersChangedMessage();
		LRESULT OnGlyphFoldersChanged();

		void Changes_MarkFresh();
		void Changes_MarkDirty();
		bool Changes_ConfirmIfDirty();

		void ShowEditor(Structs::FaceElement& element);

		// The merged font of the active face for the preview: without the deactivated elements; exports use GetMergedFont.
		std::shared_ptr<FontChanger::FixedSizeFont::fixed_size_font> GetPreviewFont();

		void UpdateFaceList();
		// Makes the list of elements show those of the active face in their order, keeping the selection.
		void UpdateFaceElementList();

		// Selects the elements of the active face in the list, and only them.
		void SelectFaceElements(const std::set<const Structs::FaceElement*>& elements);
		void UpdateFaceElementListViewItem(const Structs::FaceElement& element);

		// Compiles the font set, and takes the number of its textures as the expected one.
		CompiledFontSet CompileCurrentFontSet(ProgressDialog&, Structs::FontSet& fontSet);

		LRESULT WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

		static LRESULT WINAPI WndProcStatic(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
		static LRESULT WINAPI WndProcInitial(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
	};
}
