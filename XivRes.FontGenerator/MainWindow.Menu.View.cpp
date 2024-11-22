#include "pch.h"
#include "Structs.h"
#include "MainWindow.h"
#include "xivres/textools.h"

LRESULT App::FontEditorWindow::Menu_View_NextOrPrevFont(int direction) {
	auto i = static_cast<size_t>(ListBox_GetCurSel(m_hFacesListBox));
	if (i == static_cast<size_t>(LB_ERR)
		|| i + direction < 0
		|| i + direction >= ListBox_GetCount(m_hFacesListBox))
		return 0;

	i += direction;
	ListBox_SetCurSel(m_hFacesListBox, i);

	for (const auto& pFontSet : m_multiFontSet.FontSets) {
		if (i < pFontSet->Faces.size()) {
			m_pActiveFace = pFontSet->Faces[i].get();
			UpdateFaceElementList();
			break;
		}

		i -= static_cast<int>(pFontSet->Faces.size());
	}
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_WordWrap() {
	m_bWordWrap = !m_bWordWrap;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_Kerning() {
	m_bKerning = !m_bKerning;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_ShowLineMetrics() {
	m_bShowLineMetrics = !m_bShowLineMetrics;
	Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_View_Zoom(int zoom) {
	m_nZoom = zoom;
	Window_Redraw();
	return 0;
}
