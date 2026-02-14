#pragma once

namespace App {
	enum class GameReleaseVendor {
		None,
		SquareEnix,
		ShandaGames,
		ActozSoft,
		UserjoyGames,
	};

	class GameInstallationRepository {
		struct Installation {
			std::filesystem::path Path;
			GameReleaseVendor Vendor;
			bool Exists;
		};

		std::vector<Installation> m_paths;

	public:
		GameInstallationRepository();
		~GameInstallationRepository();

		static GameReleaseVendor DetermineGameRelease(std::filesystem::path path, std::filesystem::path& normalizedPath);
		static std::vector<std::pair<GameReleaseVendor, std::filesystem::path>> AutoDetectInstalledGameReleases();

		void LoadFrom(const FontGeneratorConfig& config);
		void SaveTo(FontGeneratorConfig& config);
	};
}
