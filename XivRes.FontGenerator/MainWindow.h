#pragma once

#include "BaseWindow.h"
#include "Structs.h"

namespace App {
	class FaceElementEditorDialog;
	class ProgressDialog;

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

		const std::vector<std::wstring> m_args;

		bool m_bChanged = false;
		bool m_bPathIsNotReal = false;
		std::filesystem::path m_path;
		Structs::MultiFontSet m_multiFontSet;
		Structs::FontSet* m_pFontSet = nullptr;
		Structs::Face* m_pActiveFace = nullptr;

		std::shared_ptr<xivres::texture::memory_mipmap_stream> m_pMipmap;
		std::map<Structs::FaceElement*, std::unique_ptr<FaceElementEditorDialog>> m_editors;
		bool m_bNeedRedraw = false;
		bool m_bWordWrap = false;
		bool m_bKerning = false;
		bool m_bShowLineMetrics = true;

		HWND m_hWnd{};
		HACCEL m_hAccelerator{};
		HFONT m_hUiFont{};

		HWND m_hFacesListBox{};
		HWND m_hFaceElementsListView{};
		HWND m_hEdit{};

		int m_nDrawLeft{};
		int m_nDrawTop{};
		int m_nZoom = 1;
		xivres::font_type m_hotReloadFontType = xivres::font_type::undefined;

		bool m_bIsReorderingFaceElementList = false;

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
		LRESULT Window_OnMouseLButtonUp(uint16_t states, int16_t x, int16_t y);
		LRESULT Window_OnDestroy();
		void Window_Redraw();

		LRESULT Menu_File_New(xivres::font_type fontType);
		LRESULT Menu_File_Open();
		LRESULT Menu_File_Save();
		LRESULT Menu_File_SaveAs(bool changeCurrentFile);
		LRESULT Menu_File_Exit();

		LRESULT Menu_Edit_Add();
		LRESULT Menu_Edit_Cut();
		LRESULT Menu_Edit_Copy();
		LRESULT Menu_Edit_Paste();
		LRESULT Menu_Edit_Delete();
		LRESULT Menu_Edit_SelectAll();
		LRESULT Menu_Edit_Details();
		LRESULT Menu_Edit_ChangeParams(int baselineShift, int horizontalOffset, int letterSpacing, float fontSize);
		LRESULT Menu_Edit_ToggleMergeMode();
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
		LRESULT Menu_Export_MapFontLobby();

		LRESULT Menu_HotReload_Reload(bool restore);
		LRESULT Menu_HotReload_Font(xivres::font_type mode);

		LRESULT Edit_OnCommand(uint16_t commandId);

		LRESULT FaceListBox_OnCommand(uint16_t commandId);

		LRESULT FaceElementsListView_OnBeginDrag(NM_LISTVIEW& nmlv);
		bool FaceElementsListView_OnDragProcessMouseUp(int16_t x, int16_t y);
		bool FaceElementsListView_OnDragProcessMouseMove(int16_t x, int16_t y);
		bool FaceElementsListView_DragProcessDragging(int16_t x, int16_t y);
		LRESULT FaceElementsListView_OnDblClick(NMITEMACTIVATE& nmia);

		void SetCurrentMultiFontSet(const std::filesystem::path& path);
		void SetCurrentMultiFontSet(Structs::MultiFontSet multiFontSet, std::filesystem::path path, bool fakePath);

		void Changes_MarkFresh();
		void Changes_MarkDirty();
		bool Changes_ConfirmIfDirty();

		void ShowEditor(Structs::FaceElement& element);

		void UpdateFaceList();
		void UpdateFaceElementList();
		void UpdateFaceElementListViewItem(const Structs::FaceElement& element);

		std::pair<std::vector<std::shared_ptr<xivres::fontdata::stream>>, std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>>> CompileCurrentFontSet(ProgressDialog&, Structs::FontSet& fontSet);

		LRESULT WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

		static LRESULT WINAPI WndProcStatic(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
		static LRESULT WINAPI WndProcInitial(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
	};
}
