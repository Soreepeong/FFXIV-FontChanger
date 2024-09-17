#pragma once

namespace GameFontReloader {
	static constexpr size_t FaceCount = 0x29;

	struct FontSetInGame {
		struct Face {
			int TexCount;
			int Padding;
			char* TexPattern;
			char* Fdt;
		} Faces[FaceCount];
	};

	struct FontSet {
		struct Face {
			int TexCount;
			std::string TexPattern;
			std::string Fdt;
		} Faces[FaceCount];
	};

	class GameProcess {
		const std::unique_ptr<void, decltype(&CloseHandle)> m_hProcess;
		const HMODULE m_hModule;
		const std::filesystem::path m_gameExePath;

		std::vector<void*> m_setAddresses;
		std::vector<FontSet> m_sets;
		std::vector<FontSetInGame> m_originals;

		void* m_ppFramework = nullptr;
		void* m_pfnFrameworkGetUiModule = nullptr;

	public:
		GameProcess(DWORD pid);

		const std::vector<FontSet>& GetFontSets() const { return m_sets; }

		void RefreshFonts(const FontSet* pTargetSet = nullptr) const;
	};

	const FontSet& GetDefaultFontSet(xivres::font_type type);
}
