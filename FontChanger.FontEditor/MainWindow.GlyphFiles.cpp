#include "pch.h"
#include "FontChanger.Presets/GlyphFiles.h"
#include "MainWindow.h"
#include "ProgressDialog.h"
#include "resource.h"

namespace {
	constexpr GUID Guid_IFileDialog_ExportGlyphs{0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x71}};

	struct ExportJob {
		std::wstring FolderName;
		std::shared_ptr<xivres::fontgen::fixed_size_font> Font;
		App::GlyphFiles::ExportOptions Options;
	};

	// Characters that cannot be in file names are replaced.
	std::wstring SanitizeFileName(std::wstring name) {
		for (auto& c : name) {
			if (c < 0x20 || std::wstring_view(L"<>:\"/\\|?*").find(c) != std::wstring_view::npos)
				c = L'_';
		}
		while (!name.empty() && (name.back() == L' ' || name.back() == L'.'))
			name.pop_back();
		return name.empty() ? L"_" : name;
	}

	std::optional<std::filesystem::path> PickFolder(HWND hWnd) {
		IFileOpenDialogPtr pDialog;
		DWORD dwFlags;
		SuccessOrThrow(pDialog.CreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER));
		SuccessOrThrow(pDialog->SetClientGuid(Guid_IFileDialog_ExportGlyphs));
		SuccessOrThrow(pDialog->SetTitle(std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTGLYPHS)).c_str()));
		SuccessOrThrow(pDialog->GetOptions(&dwFlags));
		SuccessOrThrow(pDialog->SetOptions(dwFlags | FOS_FORCEFILESYSTEM | FOS_PICKFOLDERS));
		if (SuccessOrThrow(pDialog->Show(hWnd), {HRESULT_FROM_WIN32(ERROR_CANCELLED)}) == HRESULT_FROM_WIN32(ERROR_CANCELLED))
			return std::nullopt;

		IShellItemPtr pResult;
		PWSTR pszFileName;
		SuccessOrThrow(pDialog->GetResult(&pResult));
		SuccessOrThrow(pResult->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName));
		if (!pszFileName)
			throw std::runtime_error("DEBUG: The selected file does not have a filesystem path.");
		std::unique_ptr<std::remove_pointer_t<PWSTR>, decltype(&CoTaskMemFree)> pszFileNamePtr(pszFileName, &CoTaskMemFree);
		return std::filesystem::path(pszFileName);
	}

	LRESULT RunExportJobs(HWND hWnd, const std::vector<ExportJob>& jobs) {
		return TryCatchShowError<App::ProgressDialog::ProgressDialogCancelledError>(hWnd, IDS_ERROR_EXPORTFAILURE_BODY, LRESULT{1}, [&]() -> LRESULT {
			const auto folder = PickFolder(hWnd);
			if (!folder)
				return 0;

			App::ProgressDialog progressDialog(hWnd, std::wstring(GetStringResource(IDS_WINDOWTITLE_EXPORTGLYPHS)));
			for (size_t i = 0; i < jobs.size(); i++) {
				const auto& job = jobs[i];
				const auto target = jobs.size() == 1 ? *folder : *folder / SanitizeFileName(job.FolderName);
				App::GlyphFiles::ExportGlyphs(*job.Font, target, job.Options, [&](size_t done, size_t total) {
					progressDialog.ThrowIfCancelled();
					if (done % 64 == 0 || done == total) {
						progressDialog.UpdateStatusMessage(std::vformat(GetStringResource(IDS_EXPORTPROGRESS_GLYPHS), std::make_wformat_args(done, total)));
						progressDialog.UpdateProgress((static_cast<float>(i) + (total ? static_cast<float>(done) / static_cast<float>(total) : 1.f)) / static_cast<float>(jobs.size()));
					}
				});
			}
			return 0;
		});
	}
}

void App::FontEditorWindow::UpdateProjectDirectory() {
	std::filesystem::path directory;
	if (m_currentShellItem) {
		if (PWSTR pszFileName{}; SUCCEEDED(m_currentShellItem->GetDisplayName(SIGDN_FILESYSPATH, &pszFileName)) && pszFileName) {
			directory = std::filesystem::path(pszFileName).parent_path();
			CoTaskMemFree(pszFileName);
		}
	}

	if (directory != Structs::GetProjectDirectory()) {
		Structs::SetProjectDirectory(std::move(directory));
		Structs::OnProjectDirectoryChange(m_multiFontSet);
	}
}

void App::FontEditorWindow::StartWatchingGlyphFolders() {
	GlyphFiles::SetChangeNotificationWindow(m_hWnd, GetGlyphFoldersChangedMessage());
}

void App::FontEditorWindow::StopWatchingGlyphFolders() {
	GlyphFiles::SetChangeNotificationWindow(nullptr, 0);
}

UINT App::FontEditorWindow::GetGlyphFoldersChangedMessage() {
	static const auto s_message = RegisterWindowMessageW(L"FontChanger.FontEditor.GlyphFoldersChanged");
	return s_message;
}

LRESULT App::FontEditorWindow::OnGlyphFoldersChanged() {
	const auto changed = GlyphFiles::TakeChangedFolders();
	if (changed.empty())
		return 0;

	auto redraw = false;
	for (const auto& fontSet : m_multiFontSet.FontSets) {
		for (const auto& face : fontSet->Faces) {
			auto faceChanged = false;
			for (const auto& element : face->Elements) {
				const auto& settings = element->RendererSpecific.GlyphImages;
				if (element->Renderer != Structs::RendererEnum::GlyphImages || settings.IsEmbedded() || settings.Path.empty())
					continue;
				if (std::ranges::find(changed, GlyphFiles::ResolvePath(settings.Path)) == changed.end())
					continue;

				element->OnFontCreateParametersChange();
				faceChanged = true;
				if (face.get() == m_pActiveFace)
					UpdateFaceElementListViewItem(*element);
			}
			if (faceChanged) {
				face->OnElementChange();
				redraw |= face.get() == m_pActiveFace;
			}
		}
	}
	if (redraw)
		Window_Redraw();
	return 0;
}

LRESULT App::FontEditorWindow::Menu_Export_Glyphs(bool withAdjustments) {
	if (!m_pActiveFace)
		return 0;

	std::vector<ExportJob> jobs;
	for (auto i = ListView_GetNextItem(m_hFaceElementsListView, -1, LVNI_SELECTED); i >= 0; i = ListView_GetNextItem(m_hFaceElementsListView, i, LVNI_SELECTED)) {
		if (static_cast<size_t>(i) >= m_pActiveFace->Elements.size())
			continue;

		const auto& element = *m_pActiveFace->Elements[i];
		if (element.Renderer == Structs::RendererEnum::Empty)
			continue;

		jobs.push_back({
			.FolderName = std::format(L"{} {}", i + 1, xivres::util::unicode::convert<std::wstring>(element.GetBaseFont()->family_name())),
			.Font = GlyphFiles::GetElementFontForExport(element, withAdjustments),
			.Options = {
				.WithAdjustments = withAdjustments,
				.Source = GlyphFiles::DescribeSource(element),
			},
		});
	}
	if (jobs.empty())
		return 0;

	return RunExportJobs(m_hWnd, jobs);
}

LRESULT App::FontEditorWindow::Menu_Export_FaceGlyphs() {
	if (!m_pActiveFace)
		return 0;

	return RunExportJobs(m_hWnd, {{
		.FolderName = xivres::util::unicode::convert<std::wstring>(m_pActiveFace->Name),
		.Font = m_pActiveFace->GetMergedFont(),
		.Options = {
			.WithAdjustments = true,
			.Source = GlyphFiles::DescribeSource(*m_pActiveFace),
		},
	}});
}
