#pragma once

#include "BaseWindow.h"
#include "Structs.h"

namespace App {
	class FaceElementEditorDialog;
	class ProgressDialog;

	class FontEditorWindow : public BaseWindow {
		static constexpr auto ClassName = L"FontEditorWindowClass";
		static constexpr float NoBaseFontSizes[]{9.6f, 10.f, 12.f, 14.f, 16.f, 18.f, 18.4f, 20.f, 23.f, 34.f, 36.f, 40.f, 45.f, 46.f, 68.f, 90.f,};

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

		LRESULT WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
			switch (msg) {
				case WM_COMMAND:
					switch (LOWORD(wParam)) {
						case Id_Edit: return Edit_OnCommand(HIWORD(wParam));
						case Id_FaceListBox: return FaceListBox_OnCommand(HIWORD(wParam));
						case ID_FILE_NEW_MAINGAMEFONT: return Menu_File_New(xivres::font_type::font);
						case ID_FILE_NEW_LOBBYFONT: return Menu_File_New(xivres::font_type::font_lobby);
						case ID_FILE_NEW_CHNAXIS: return Menu_File_New(xivres::font_type::chn_axis);
						case ID_FILE_NEW_KRNAXIS: return Menu_File_New(xivres::font_type::krn_axis);
						case ID_FILE_OPEN: return Menu_File_Open();
						case ID_FILE_SAVE: return Menu_File_Save();
						case ID_FILE_SAVEAS: return Menu_File_SaveAs(true);
						case ID_FILE_SAVECOPYAS: return Menu_File_SaveAs(false);
						case ID_FILE_EXIT: return Menu_File_Exit();
						case ID_EDIT_ADD: return Menu_Edit_Add();
						case ID_EDIT_CUT: return Menu_Edit_Cut();
						case ID_EDIT_COPY: return Menu_Edit_Copy();
						case ID_EDIT_PASTE: return Menu_Edit_Paste();
						case ID_EDIT_DELETE: return Menu_Edit_Delete();
						case ID_EDIT_SELECTALL: return Menu_Edit_SelectAll();
						case ID_EDIT_DETAILS: return Menu_Edit_Details();
						case ID_EDIT_DECREASEBASELINESHIFT: return Menu_Edit_ChangeParams(-1, 0, 0, 0);
						case ID_EDIT_INCREASEBASELINESHIFT: return Menu_Edit_ChangeParams(+1, 0, 0, 0);
						case ID_EDIT_DECREASEHORIZONTALOFFSET: return Menu_Edit_ChangeParams(0, -1, 0, 0);
						case ID_EDIT_INCREASEHORIZONTALOFFSET: return Menu_Edit_ChangeParams(0, +1, 0, 0);
						case ID_EDIT_DECREASELETTERSPACING: return Menu_Edit_ChangeParams(0, 0, -1, 0);
						case ID_EDIT_INCREASELETTERSPACING: return Menu_Edit_ChangeParams(0, 0, +1, 0);
						case ID_EDIT_DECREASEFONTSIZEBY1: return Menu_Edit_ChangeParams(0, 0, 0, -1.f);
						case ID_EDIT_INCREASEFONTSIZEBY1: return Menu_Edit_ChangeParams(0, 0, 0, +1.f);
						case ID_EDIT_DECREASEFONTSIZEBY0_2: return Menu_Edit_ChangeParams(0, 0, 0, -0.2f);
						case ID_EDIT_INCREASEFONTSIZEBY0_2: return Menu_Edit_ChangeParams(0, 0, 0, +0.2f);
						case ID_EDIT_TOGGLEMERGEMODE: return Menu_Edit_ToggleMergeMode();
						case ID_EDIT_MOVEUP: return Menu_Edit_MoveUpOrDown(-1);
						case ID_EDIT_MOVEDOWN: return Menu_Edit_MoveUpOrDown(+1);
						case ID_EDIT_CREATEEMPTYCOPYFROMSELECTION: return Menu_Edit_CreateEmptyCopyFromSelection();
						case ID_VIEW_PREVIOUSFONT: return Menu_View_NextOrPrevFont(-1);
						case ID_VIEW_NEXTFONT: return Menu_View_NextOrPrevFont(1);
						case ID_VIEW_WORDWRAP: return Menu_View_WordWrap();
						case ID_VIEW_KERNING: return Menu_View_Kerning();
						case ID_VIEW_SHOWLINEMETRICS: return Menu_View_ShowLineMetrics();
						case ID_VIEW_100: return Menu_View_Zoom(1);
						case ID_VIEW_200: return Menu_View_Zoom(2);
						case ID_VIEW_300: return Menu_View_Zoom(3);
						case ID_VIEW_400: return Menu_View_Zoom(4);
						case ID_VIEW_500: return Menu_View_Zoom(5);
						case ID_VIEW_600: return Menu_View_Zoom(6);
						case ID_VIEW_700: return Menu_View_Zoom(7);
						case ID_VIEW_800: return Menu_View_Zoom(8);
						case ID_VIEW_900: return Menu_View_Zoom(9);
						case ID_EXPORT_PREVIEW: return Menu_Export_Preview();
						case ID_EXPORT_RAW: return Menu_Export_Raw();
						case ID_EXPORT_TOTTMP_COMPRESSWHILEPACKING: return Menu_Export_TTMP(CompressionMode::CompressWhilePacking);
						case ID_EXPORT_TOTTMP_COMPRESSAFTERPACKING: return Menu_Export_TTMP(CompressionMode::CompressAfterPacking);
						case ID_EXPORT_TOTTMP_DONOTCOMPRESS: return Menu_Export_TTMP(CompressionMode::DoNotCompress);
						case ID_HOTRELOAD_RELOAD: return Menu_HotReload_Reload(false);
						case ID_HOTRELOAD_RESTORE: return Menu_HotReload_Reload(true);
						case ID_HOTRELOAD_FONT_AUTO: return Menu_HotReload_Font(xivres::font_type::undefined);
						case ID_HOTRELOAD_FONT_FONT: return Menu_HotReload_Font(xivres::font_type::font);
						case ID_HOTRELOAD_FONT_LOBBY: return Menu_HotReload_Font(xivres::font_type::font_lobby);
						case ID_HOTRELOAD_FONT_CHNAXIS: return Menu_HotReload_Font(xivres::font_type::chn_axis);
						case ID_HOTRELOAD_FONT_KRNAXIS: return Menu_HotReload_Font(xivres::font_type::krn_axis);
					}
					break;

				case WM_NOTIFY:
					switch (auto& hdr = *reinterpret_cast<NMHDR*>(lParam); hdr.idFrom) {
						case Id_FaceElementListView:
							switch (hdr.code) {
								case LVN_BEGINDRAG: return FaceElementsListView_OnBeginDrag(*(reinterpret_cast<NM_LISTVIEW*>(lParam)));
								case NM_DBLCLK: return FaceElementsListView_OnDblClick(*(reinterpret_cast<NMITEMACTIVATE*>(lParam)));
							}
							break;
					}
					return 0;

				case WM_CREATE: return Window_OnCreate(hwnd);
				case WM_MOUSEMOVE: return Window_OnMouseMove(static_cast<uint16_t>(wParam), LOWORD(lParam), HIWORD(lParam));
				case WM_LBUTTONUP: return Window_OnMouseLButtonUp(static_cast<uint16_t>(wParam), LOWORD(lParam), HIWORD(lParam));
				case WM_SIZE: return Window_OnSize();
				case WM_PAINT: return Window_OnPaint();
				case WM_INITMENUPOPUP: return Window_OnInitMenuPopup(reinterpret_cast<HMENU>(wParam), LOWORD(lParam), !!HIWORD(lParam));
				case WM_CLOSE: return Menu_File_Exit();
				case WM_DESTROY: return Window_OnDestroy();
			}

			return DefWindowProcW(hwnd, msg, wParam, lParam);
		}

		static LRESULT WINAPI WndProcStatic(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
		static LRESULT WINAPI WndProcInitial(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
	};
}
