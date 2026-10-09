#include "pch.h"
#include "Structs.h"
#include "GameFontReloader.h"
#include "MainWindow.h"
#include "xivres/textools.h"

LRESULT App::FontEditorWindow::Menu_HotReload_Reload(bool restore) {
	std::vector<DWORD> pids;
	pids.resize(4096);
	DWORD cb;
	EnumProcesses(pids.data(), static_cast<DWORD>(std::span(pids).size_bytes()), &cb);
	pids.resize(cb / sizeof DWORD);
	for (const auto pid : pids) {
		try {
			GameFontReloader::GameProcess game(pid);
			if (restore) {
				game.RefreshFonts(nullptr);
				continue;
			}

			GameFontReloader::FontSet fontSet;
			xivres::font_type baseFontType = m_hotReloadFontType;

			if (baseFontType == xivres::font_type::undefined) {
				if (game.GetFontSets().size() == 5)
					baseFontType = xivres::font_type::chn_axis;
				else if (game.GetFontSets().size() == 4)
					baseFontType = xivres::font_type::krn_axis;
				else
					baseFontType = xivres::font_type::font;

				fontSet = GameFontReloader::GetDefaultFontSet(baseFontType);

				for (const auto& f : m_multiFontSet.FontSets) {
					std::string texNameFormat = f->TexFilenameFormat;
					for (size_t pos; (pos = texNameFormat.find("{}")) != std::string::npos;)
						texNameFormat.replace(pos, 2, "%d");

					for (const auto& face : f->Faces) {
						for (auto& f2 : fontSet.Faces) {
							if (f2.Fdt == face->Name + ".fdt" && f2.TexPattern != texNameFormat) {
								f2.TexPattern = texNameFormat;
								f2.TexCount = f->ExpectedTexCount;
							}
						}
					}
				}
			} else
				fontSet = GameFontReloader::GetDefaultFontSet(baseFontType);

			if (baseFontType == xivres::font_type::chn_axis && game.GetFontSets().size() < 5)
				continue;

			game.RefreshFonts(&fontSet);
		} catch (const std::exception& e) {
			// pass, don't really care
			OutputDebugStringA(std::format("Failed to reload fonts: {}\n", e.what()).c_str());
		}
	}
	return 0;
}

LRESULT App::FontEditorWindow::Menu_HotReload_Font(xivres::font_type mode) {
	m_hotReloadFontType = mode;
	return 0;
}
