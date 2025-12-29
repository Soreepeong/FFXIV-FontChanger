#pragma once

#include <nlohmann/json_fwd.hpp>

struct FontGeneratorConfig {
	std::vector<std::filesystem::path> Global;
	std::vector<std::filesystem::path> China;
	std::vector<std::filesystem::path> Korea;
	std::vector<std::filesystem::path> TraditionalChinese;

	std::string Language;

	int FaceElementListViewHeight;
	int PreviewEditHeight;

	bool Dirty;

	static const FontGeneratorConfig Default;

	static std::filesystem::path GetConfigPath();

	void MarkDirty();
	void SaveIfDirty();

	void Save() const;
};

void from_json(const nlohmann::json& json, FontGeneratorConfig& value);
void to_json(nlohmann::json& json, const FontGeneratorConfig& value);
