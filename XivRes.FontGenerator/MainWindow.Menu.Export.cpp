#include "pch.h"
#include "Structs.h"
#include "ExportPreviewWindow.h"
#include "MainWindow.h"
#include "MainWindow.Internal.h"
#include "ProgressDialog.h"
#include "xivres/textools.h"

LRESULT App::FontEditorWindow::Menu_Export_Preview() {
	using namespace xivres::fontgen;

	try {
		ProgressDialog progressDialog(m_hWnd, "Exporting...");
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
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
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
		SuccessOrThrow(pDialog->SetTitle(L"Export raw"));
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

		ProgressDialog progressDialog(m_hWnd, "Exporting...");
		ShowWindow(m_hWnd, SW_HIDE);
		const auto hideWhilePacking = xivres::util::on_dtor([this]() { ShowWindow(m_hWnd, SW_SHOW); });

		for (const auto& pFontSet : m_multiFontSet.FontSets) {
			const auto [fdts, mips] = CompileCurrentFontSet(progressDialog, *pFontSet);

			progressDialog.UpdateProgress(std::nanf(""));
			progressDialog.UpdateStatusMessage("Writing to files...");

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

				if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
					const auto path2 = basePath / std::format("font_lobby{}.tex", i1);
					std::filesystem::copy(path, path2, std::filesystem::copy_options::overwrite_existing);
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

				if (m_multiFontSet.ExportMapFontLobbyToFont && pFontSet->TexFilenameFormat == "font{}.tex") {
					const auto path2 = basePath / std::format("{}_lobby.fdt", pFontSet->Faces[i]->Name);
					std::filesystem::copy(path, path2, std::filesystem::copy_options::overwrite_existing);
				}
			}
		}
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
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
		SuccessOrThrow(pDialog->SetFileTypes(fileTypesSpan.size(), fileTypesSpan.data()));
		SuccessOrThrow(pDialog->SetFileTypeIndex(0));
		SuccessOrThrow(pDialog->SetTitle(L"Save"));
		SuccessOrThrow(pDialog->SetFileName(std::format(L"{}.ttmp2", m_path.filename().replace_extension(L"").wstring()).c_str()));
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

		ProgressDialog progressDialog(m_hWnd, "Exporting...");
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
				progressDialog.UpdateStatusMessage(std::format("Packing file: {}", targetFileName));

				writer.add_packed(xivres::compressing_packed_stream<xivres::standard_compressing_packer>(targetFileName, fdts[i], compressionMode == CompressionMode::CompressWhilePacking ? Z_BEST_COMPRESSION : Z_NO_COMPRESSION));
			}

			for (size_t i = 0; i < mips.size(); i++) {
				progressDialog.ThrowIfCancelled();

				const auto i1 = i + 1;
				const auto targetFileName = std::format("common/font/{}", std::vformat(pFontSet->TexFilenameFormat, std::make_format_args(i1)));
				progressDialog.UpdateStatusMessage(std::format("Packing file: {}", targetFileName));

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
		}
		writer.close();
	} catch (const ProgressDialog::ProgressDialogCancelledError&) {
		return 1;
	} catch (const std::exception& e) {
		MessageBoxW(m_hWnd, std::format(L"Failed to export: {}", xivres::util::unicode::convert<std::wstring>(e.what())).c_str(), GetWindowString(m_hWnd).c_str(), MB_OK | MB_ICONERROR);
		return 1;
	}

	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_MapFontLobby() {
	m_multiFontSet.ExportMapFontLobbyToFont = !m_multiFontSet.ExportMapFontLobbyToFont;
	Changes_MarkDirty();
	return 0;
}
