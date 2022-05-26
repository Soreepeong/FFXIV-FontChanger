#pragma once

#include "BaseWindow.h"

namespace App {
	class ExportPreviewWindow : public BaseWindow {
		static constexpr auto ClassName = L"ExportPreviewWindowClass";

		enum : size_t {
			Id_None,
			Id_FaceListBox,
			Id_Edit,
			Id__Last,
		};
		static constexpr auto FaceListBoxWidth = 160;
		static constexpr auto EditHeight = 160;

		std::shared_ptr<XivRes::MemoryMipmapStream> m_pMipmap;
		bool m_bNeedRedraw = false;

		std::vector<std::pair<std::string, std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>>> m_fonts;

		HWND m_hWnd{};
		HFONT m_hUiFont{};

		HWND m_hFacesListBox{};
		HWND m_hEdit{};

		int m_nDrawLeft{};
		int m_nDrawTop{};

		LRESULT Window_OnCreate(HWND hwnd);

		LRESULT Window_OnSize();

		LRESULT Window_OnPaint();

		LRESULT Window_OnDestroy();

		LRESULT Edit_OnCommand(uint16_t commandId);

		LRESULT FaceListBox_OnCommand(uint16_t commandId);

		LRESULT WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

		static LRESULT WINAPI WndProcStatic(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

		static LRESULT WINAPI WndProcInitial(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

		ExportPreviewWindow(std::vector<std::pair<std::string, std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>>> fonts);

	public:
		static void ShowNew(std::vector<std::pair<std::string, std::shared_ptr<XivRes::FontGenerator::IFixedSizeFont>>> fonts) {
			new ExportPreviewWindow(std::move(fonts));
		}

		bool ConsumeDialogMessage(MSG& msg) override;

		bool ConsumeAccelerator(MSG& msg) override;
	};
}
