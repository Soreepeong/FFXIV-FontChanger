#include "pch.h"
#include "Structs.h"
#include "ExportPreviewWindow.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "ProgressDialog.h"
#include "xivres/textools.h"
#include "resource.h"

LRESULT App::FontEditorWindow::Menu_Export_Preview() {
	using namespace xivres::fontgen;

	try {
		ProgressDialog progressDialog(m_hWnd, std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTRAW)));
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		std::vector<std::pair<std::string, std::shared_ptr<fixed_size_font>>> resultFonts;
		for (const auto& fontSet : m_multiFontSet.FontSets) {
			const auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *fontSet);

			auto texturesAll = std::make_shared<xivres::texture::stream>(mips[0]->Type, mips[0]->Width, mips[0]->Height, 1, 1, mips.size());
			for (size_t i = 0; i < mips.size(); i++)
				texturesAll->set_mipmap(0, i, mips[i]);

			for (size_t i = 0; i < fdts.size(); i++)
				resultFonts.emplace_back(fontSet->Faces[i]->Name, std::make_shared<fontdata_fixed_size_font>(fdts[i], mips, fontSet->Faces[i]->Name, ""));

			std::thread([texturesAll]() { preview(*texturesAll); }).detach();
		}

		ExportPreviewWindow::ShowNew(std::move(resultFonts));

		return 0;
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const WException& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::system_error& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::exception& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	}
}

LRESULT App::FontEditorWindow::Menu_Export_Raw() {
	using namespace xivres::fontgen;

	try {
		IFileOpenDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Export));
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTRAW)).c_str()));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM | FOS_PICKFOLDERS));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		IShellItemPtr pResult;
		PWSTR pszFileName;
		SuccessOrThrow(pDialog->GetResult(&pResult));
		SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
		if (!pszFileName)
			throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");

		std::unique_ptr<std::remove_pointer_t<PWSTR>, decltype(&CoTaskMemFree)> pszFileNamePtr(pszFileName, &CoTaskMemFree);
		const auto basePath = std::filesystem::path(pszFileName);

		ProgressDialog progressDialog(m_hWnd, std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTRAW)));
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		for (const auto& pFontSet : m_multiFontSet.FontSets) {
			const auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *pFontSet);

			progressDialog.UpdateProgress(std::nanf(""));
			progressDialog.UpdateStatusMessage(GetStringResource(IDS_EXPORTPROGRESS_WRITINGTOFILES));

			std::vector<char> buf(32768);
			xivres::texture::stream textureOne(mips[0]->Type, mips[0]->Width, mips[0]->Height, 1, 1, 1);

			for (size_t i = 0; i < mips.size(); i++) {
				progressDialog.ThrowIfCancelled();
				textureOne.set_mipmap(0, 0, mips[i]);

				const auto i1 = i + 1;
				const auto path = basePath / std::vformat(pFontSet->TexFilenameFormat, std::make_format_args(i1));
				std::ofstream out(path, std::ios::binary);

				for (size_t read, pos = 0; (read = textureOne.read(pos, buf.data(), buf.size())); pos += read) {
					progressDialog.ThrowIfCancelled();
					out.write(buf.data(), read);
				}

				out.close();

				if (pFontSet->TexFilenameFormat == "font{}.tex") {
					if (m_multiFontSet.ExportMapFontLobbyToFont) {
						copy(
							path,
							basePath / std::format("font_lobby{}.tex", i1),
							std::filesystem::copy_options::overwrite_existing);
					}
					if (m_multiFontSet.ExportMapChnAxisToFont) {
						copy(
							path,
							basePath / std::format("font_chn_{}.tex", i1),
							std::filesystem::copy_options::overwrite_existing);
					}
					if (m_multiFontSet.ExportMapFontLobbyToFont) {
						copy(
							path,
							basePath / std::format("font_krn_{}.tex", i1),
							std::filesystem::copy_options::overwrite_existing);
					}
				}
			}

			for (size_t i = 0; i < fdts.size(); i++) {
				progressDialog.ThrowIfCancelled();
				const auto path = basePath / std::format("{}.fdt", pFontSet->Faces[i]->Name);
				std::ofstream out(path, std::ios::binary);

				for (size_t read, pos = 0; (read = fdts[i]->read(pos, buf.data(), buf.size())); pos += read) {
					progressDialog.ThrowIfCancelled();
					out.write(buf.data(), read);
				}

				out.close();

				if (pFontSet->TexFilenameFormat == "font{}.tex") {
					if (m_multiFontSet.ExportMapFontLobbyToFont) {
						copy(
							path,
							basePath / std::format("{}_lobby.fdt", pFontSet->Faces[i]->Name),
							std::filesystem::copy_options::overwrite_existing);
					}
					if (m_multiFontSet.ExportMapChnAxisToFont) {
						if (pFontSet->Faces[i]->Name == "AXIS_12")
							copy(path, basePath / "ChnAXIS_120.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_14")
							copy(path, basePath / "ChnAXIS_140.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_18")
							copy(path, basePath / "ChnAXIS_180.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_36")
							copy(path, basePath / "ChnAXIS_360.fdt", std::filesystem::copy_options::overwrite_existing);
					}
					if (m_multiFontSet.ExportMapKrnAxisToFont) {
						if (pFontSet->Faces[i]->Name == "AXIS_12")
							copy(path, basePath / "KrnAXIS_120.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_14")
							copy(path, basePath / "KrnAXIS_140.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_18")
							copy(path, basePath / "KrnAXIS_180.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_36")
							copy(path, basePath / "KrnAXIS_360.fdt", std::filesystem::copy_options::overwrite_existing);
					}
					if (m_multiFontSet.ExportMapTCAxisToFont) {
						if (pFontSet->Faces[i]->Name == "AXIS_12")
							copy(path, basePath / "tcaxis_120.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_14")
							copy(path, basePath / "tcaxis_140.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_18")
							copy(path, basePath / "tcaxis_180.fdt", std::filesystem::copy_options::overwrite_existing);
						else if (pFontSet->Faces[i]->Name == "AXIS_36")
							copy(path, basePath / "tcaxis_360.fdt", std::filesystem::copy_options::overwrite_existing);
					}
				}
			}
		}
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const WException& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::system_error& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::exception& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_TTMP(CompressionMode compressionMode) {
	using namespace xivres::fontgen;
	static constexpr COMDLG_FILTERSPEC fileTypes[] = {
		{L"TTMP2 file (*.ttmp2)", L"*.ttmp2"},
		{L"ZIP file (*.zip)", L"*.zip"},
		{L"All files (*.*)", L"*"},
	};
	const auto fileTypesSpan = std::span(fileTypes);

	std::wstring finalPath;
	try {
		IFileSaveDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_Export));
		SuccessOrThrow(pDialog->SetFileTypes(static_cast<UINT>(fileTypesSpan.size()), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTTTMP)).c_str()));
		SuccessOrThrow(pDialog->SetFileName(std::format(L"{}.ttmp2", std::filesystem::path(GetCurrentFileName()).replace_extension(L"").wstring()).c_str()));
		SuccessOrThrow(pDialog->SetDefaultExtension(L"json"));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM));
		switch (SuccessOrThrow(pDialog->Show(m_hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)})) {
			case HRESULT_FROM_WIN32(ERROR_CANCELLED):
				return 0;
		}

		{
			IShellItemPtr pResult;
			PWSTR pszFileName;
			SuccessOrThrow(pDialog->GetResult(&pResult));
			SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
			if (!pszFileName)
				throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");

			finalPath = pszFileName;
			CoTaskMemFree(pszFileName);
		}

		xivres::textools::simple_ttmp2_writer writer(finalPath);

		ProgressDialog progressDialog(m_hWnd, std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTTTMP)));
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		std::vector<std::tuple<std::vector<std::shared_ptr<xivres::fontdata::stream>>, std::vector<std::shared_ptr<xivres::texture::memory_mipmap_stream>>, const Structs::FontSet&>> pairs;

		writer.begin_packed(compressionMode == CompressionMode::CompressAfterPacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION);
		for (auto& pFontSet : m_multiFontSet.FontSets) {
			auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *pFontSet);

			auto& modsList = writer.ttmpl().SimpleModsList;
			const auto beginIndex = modsList.size();

			for (size_t i = 0; i < fdts.size(); i++) {
				progressDialog.ThrowIfCancelled();

				const auto targetFileName = std::format("common/font/{}.fdt", pFontSet->Faces[i]->Name);
				const auto targetFileNameW = xivres::util::unicode::convert<std::wstring>(targetFileName);
				progressDialog.UpdateStatusMessage(
					std::vformat(
						GetStringResource(IDS_EXPORTPROGRESS_WRITINGFILE),
						std::make_wformat_args(targetFileNameW)));

				writer.add_packed(xivres::compressing_packed_stream<xivres::standard_compressing_packer>(targetFileName, fdts[i], compressionMode == CompressionMode::CompressWhilePacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION));
			}

			for (size_t i = 0; i < mips.size(); i++) {
				progressDialog.ThrowIfCancelled();

				const auto i1 = i + 1;
				const auto targetFileName = std::format("common/font/{}", std::vformat(pFontSet->TexFilenameFormat, std::make_format_args(i1)));
				const auto targetFileNameW = xivres::util::unicode::convert<std::wstring>(targetFileName);
				progressDialog.UpdateStatusMessage(
					std::vformat(
						GetStringResource(IDS_EXPORTPROGRESS_WRITINGFILE),
						std::make_wformat_args(targetFileNameW)));

				const auto& mip = mips[i];
				auto textureOne = std::make_shared<xivres::texture::stream>(mip->Type, mip->Width, mip->Height, 1, 1, 1);
				textureOne->set_mipmap(0, 0, mip);

				writer.add_packed(xivres::compressing_packed_stream<xivres::texture_compressing_packer>(targetFileName, std::move(textureOne), compressionMode == CompressionMode::CompressWhilePacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION));
			}

			const auto endIndex = modsList.size();

			if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
				modsList.reserve(modsList.size() + endIndex - beginIndex);
				for (size_t i = beginIndex; i < endIndex; i++) {
					xivres::textools::mods_json tmp = modsList[i];
					if (tmp.FullPath.ends_with(".fdt")) {
						tmp.Name.erase(tmp.Name.size() - 4, 4);
						tmp.Name.append("_lobby.fdt");
					} else if (tmp.FullPath.ends_with(".tex")) {
						tmp.Name.insert(tmp.Name.size() - 5, "_lobby");
					} else {
						continue;
					}

					tmp.FullPath = xivres::util::unicode::convert<std::string>(tmp.Name, &xivres::util::unicode::lower);
					modsList.push_back(tmp);
				}
			}

			if (m_multiFontSet.ExportMapChnAxisToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
				modsList.reserve(modsList.size() + endIndex - beginIndex);
				for (size_t i = beginIndex; i < endIndex; i++) {
					xivres::textools::mods_json tmp = modsList[i];
					if (tmp.Name.ends_with("/AXIS_12.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("ChnAXIS_120.fdt");
					} else if (tmp.Name.ends_with("/AXIS_14.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("ChnAXIS_140.fdt");
					} else if (tmp.Name.ends_with("/AXIS_18.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("ChnAXIS_180.fdt");
					} else if (tmp.Name.ends_with("/AXIS_36.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("ChnAXIS_360.fdt");
					} else if (tmp.Name.ends_with(".tex")) {
						tmp.Name.insert(tmp.Name.size() - 5, "_chn_");
					} else {
						continue;
					}

					tmp.FullPath = xivres::util::unicode::convert<std::string>(tmp.Name, &xivres::util::unicode::lower);
					modsList.push_back(tmp);
				}
			}

			if (m_multiFontSet.ExportMapKrnAxisToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
				modsList.reserve(modsList.size() + endIndex - beginIndex);
				for (size_t i = beginIndex; i < endIndex; i++) {
					xivres::textools::mods_json tmp = modsList[i];
					if (tmp.Name.ends_with("/AXIS_12.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("KrnAXIS_120.fdt");
					} else if (tmp.Name.ends_with("/AXIS_14.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("KrnAXIS_140.fdt");
					} else if (tmp.Name.ends_with("/AXIS_18.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("KrnAXIS_180.fdt");
					} else if (tmp.Name.ends_with("/AXIS_36.fdt")) {
						tmp.Name.erase(tmp.Name.size() - 11, 11);
						tmp.Name.append("KrnAXIS_360.fdt");
					} else if (tmp.Name.ends_with(".tex")) {
						tmp.Name.insert(tmp.Name.size() - 5, "_krn_");
					} else {
						continue;
					}

					tmp.FullPath = xivres::util::unicode::convert<std::string>(tmp.Name, &xivres::util::unicode::lower);
					modsList.push_back(tmp);
				}
			}
		}
		writer.close();
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const WException& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::system_error& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	} catch (const std::exception& e) {
		ShowErrorMessageBox(m_hWnd, IDS_ERROR_EXPORTFAILURE_BODY, e);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontLobby() {
	m_multiFontSet.ExportMapFontLobbyToFont = !m_multiFontSet.ExportMapFontLobbyToFont;
	Changes_MarkDirty();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontChnAxis() {
	m_multiFontSet.ExportMapChnAxisToFont = !m_multiFontSet.ExportMapChnAxisToFont;
	Changes_MarkDirty();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontKrnAxis() {
	m_multiFontSet.ExportMapKrnAxisToFont = !m_multiFontSet.ExportMapKrnAxisToFont;
	Changes_MarkDirty();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontTCAxis() {
	m_multiFontSet.ExportMapTCAxisToFont = !m_multiFontSet.ExportMapTCAxisToFont;
	Changes_MarkDirty();
	return 0;
}
