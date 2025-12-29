#include "pch.h"
#include "FontGeneratorConfig.h"
#include "MainWindow.Internal.h"

const FontGeneratorConfig FontGeneratorConfig::Default{
	.Global = {
		R"(C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game)",
	},
	.China = {
		u8R"(C:\Program Files (x86)\上海数龙科技有限公司\最终幻想XIV\game)",
	},
	.Korea = {
		R"(C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game)",
	},
	.TraditionalChinese = {
		R"(C:\Program Files (x86)\USERJOY GAMES\FINAL FANTASY XIV TC\game)",
	},
	.FaceElementListViewHeight = ::FaceElementListViewHeight,
	.PreviewEditHeight = ::PreviewEditHeight,
};

void from_json(const nlohmann::json& json, FontGeneratorConfig& value) {
	value = {};
	if (auto it = json.find("global"); it != json.end() && it->is_array()) {
		for (const auto& [_, p] : it->items())
			value.Global.emplace_back(xivres::util::unicode::convert<std::wstring>(p.get<std::string>()));
	}

	if (auto it = json.find("china"); it != json.end() && it->is_array()) {
		for (const auto& [_, p] : it->items())
			value.China.emplace_back(xivres::util::unicode::convert<std::wstring>(p.get<std::string>()));
	}

	if (auto it = json.find("korea"); it != json.end() && it->is_array()) {
		for (const auto& [_, p] : it->items())
			value.Korea.emplace_back(xivres::util::unicode::convert<std::wstring>(p.get<std::string>()));
	}
	
	if (auto it = json.find("traditionalchinese"); it != json.end() && it->is_array()) {
		for (const auto& [_, p] : it->items())
			value.TraditionalChinese.emplace_back(xivres::util::unicode::convert<std::wstring>(p.get<std::string>()));
	}

	value.FaceElementListViewHeight = (std::max)(
		json.value("FaceElementListViewHeight", FaceElementListViewHeight),
		MinFaceElementListViewHeight);
	value.PreviewEditHeight = (std::max)(
		json.value("PreviewEditHeight", PreviewEditHeight),
		MinPreviewEditHeight);

	value.Language = json.value("Language", "");
}

void to_json(nlohmann::json& json, const FontGeneratorConfig& value) {
	json = nlohmann::json::object();

	auto arr = nlohmann::json::array();
	for (const auto& p : value.Global)
		arr.emplace_back(xivres::util::unicode::convert<std::string>(p.wstring()));
	json.emplace("global", std::move(arr));

	arr = {};
	for (const auto& p : value.China)
		arr.emplace_back(xivres::util::unicode::convert<std::string>(p.wstring()));
	json.emplace("china", std::move(arr));

	arr = {};
	for (const auto& p : value.Korea)
		arr.emplace_back(xivres::util::unicode::convert<std::string>(p.wstring()));
	json.emplace("korea", std::move(arr));
	
	arr = {};
	for (const auto& p : value.TraditionalChinese)
		arr.emplace_back(xivres::util::unicode::convert<std::string>(p.wstring()));
	json.emplace("traditionalchinese", std::move(arr));

	json.emplace("FaceElementListViewHeight", value.FaceElementListViewHeight);
	json.emplace("PreviewEditHeight", value.PreviewEditHeight);

	json.emplace("Language", value.Language);
}

std::filesystem::path FontGeneratorConfig::GetConfigPath() {
	std::wstring path(PATHCCH_MAX_CCH + 1, L'\0');
	path.resize(
		GetModuleFileNameW(
			GetModuleHandle(nullptr),
			path.data(),
			static_cast<DWORD>(path.size())));

	return std::filesystem::path(path).parent_path() / "config.json";
}

void FontGeneratorConfig::MarkDirty() {
	Dirty = true;
}

void FontGeneratorConfig::SaveIfDirty() {
	if (!Dirty)
		return;

	Dirty = false;
	Save();
}

void FontGeneratorConfig::Save() const {
	std::ofstream configFile(GetConfigPath());
	nlohmann::json json;
	to_json(json, *this);
	configFile << json;
}
