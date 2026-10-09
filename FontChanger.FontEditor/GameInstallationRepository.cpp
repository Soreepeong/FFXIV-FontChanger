#include "pch.h"
#include "GameInstallationRepository.h"

static std::wstring GetPublisherCountryName(const std::filesystem::path& path) {
	// See: https://docs.microsoft.com/en-US/troubleshoot/windows/win32/get-information-authenticode-signed-executables

	constexpr auto ENCODING = X509_ASN_ENCODING | PKCS_7_ASN_ENCODING;

	HCRYPTMSG hMsg = nullptr;
	HCERTSTORE hStore = nullptr;
	DWORD dwEncoding = 0, dwContentType = 0, dwFormatType = 0;
	if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE,
		path.c_str(),
		CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
		CERT_QUERY_FORMAT_FLAG_BINARY,
		0,
		&dwEncoding,
		&dwContentType,
		&dwFormatType,
		&hStore,
		&hMsg,
		nullptr))
		return {};

	DWORD cbData = 0;
	if (!CryptMsgGetParam(hMsg, CMSG_SIGNER_INFO_PARAM, 0, nullptr, &cbData)) {
		CryptMsgClose(hMsg);
		CertCloseStore(hStore, 0);
		return {};
	}

	std::vector<uint8_t> signerInfoBuf(cbData, {});
	if (!CryptMsgGetParam(hMsg, CMSG_SIGNER_INFO_PARAM, 0, signerInfoBuf.data(), &cbData)) {
		CryptMsgClose(hMsg);
		CertCloseStore(hStore, 0);
		return {};
	}

	const auto& signerInfo = *reinterpret_cast<CMSG_SIGNER_INFO*>(&signerInfoBuf[0]);

	CERT_INFO certInfo{};
	certInfo.Issuer = signerInfo.Issuer;
	certInfo.SerialNumber = signerInfo.SerialNumber;
	const auto pCertContext = CertFindCertificateInStore(hStore, ENCODING, 0, CERT_FIND_SUBJECT_CERT, &certInfo, nullptr);

	std::wstring country;
	if (pCertContext) {
		const auto pvTypePara = const_cast<char*>(szOID_COUNTRY_NAME);
		country.resize(CertGetNameStringW(pCertContext, CERT_NAME_ATTR_TYPE, 0, pvTypePara, nullptr, 0));
		country.resize(CertGetNameStringW(pCertContext, CERT_NAME_ATTR_TYPE, 0, pvTypePara, &country[0], static_cast<DWORD>(country.size())) - 1);
	}

	CertFreeCertificateContext(pCertContext);

	CryptMsgClose(hMsg);
	CertCloseStore(hStore, 0);
	return country;
}

static std::wstring ReadRegistryAsString(HKEY rootKey, const wchar_t* lpSubKey, const wchar_t* lpValueName, int mode = 0) {
	if (mode == 0) {
		auto res1 = ReadRegistryAsString(rootKey, lpSubKey, lpValueName, KEY_WOW64_32KEY);
		if (res1.empty())
			res1 = ReadRegistryAsString(rootKey, lpSubKey, lpValueName, KEY_WOW64_64KEY);
		return res1;
	}
	HKEY hKey;
	if (RegOpenKeyExW(rootKey,
		lpSubKey,
		0, KEY_READ | mode, &hKey))
		return {};
	const auto hKeyCleanup = std::unique_ptr<std::remove_pointer_t<HKEY>, decltype(&RegCloseKey)>(hKey, &RegCloseKey);

	DWORD buflen = 0;
	if (RegQueryValueExW(hKey, lpValueName, nullptr, nullptr, nullptr, &buflen))
		return {};

	std::wstring buf;
	buf.resize(buflen + 1);
	if (RegQueryValueExW(hKey, lpValueName, nullptr, nullptr, reinterpret_cast<LPBYTE>(&buf[0]), &buflen))
		return {};

	buf.erase(std::ranges::find(buf, L'\0'), buf.end());

	return buf;
}

App::GameInstallationRepository::GameInstallationRepository() {
}

App::GameInstallationRepository::~GameInstallationRepository() {
}

App::GameReleaseVendor App::GameInstallationRepository::DetermineGameRelease(std::filesystem::path path, std::filesystem::path& normalizedPath) {
	for (std::filesystem::path gameVersionPath; !exists(gameVersionPath = path / "game" / "ffxivgame.ver"); ) {
		auto parentPath = path.parent_path();
		if (parentPath == path)
			throw std::runtime_error("Game installation not found");
		path = std::move(parentPath);
	}

	if (GetPublisherCountryName(path / L"boot" / L"ffxivboot.exe") == L"JP") {
		normalizedPath = path / L"game";
		return GameReleaseVendor::SquareEnix;
	}

	if (GetPublisherCountryName(path / L"FFXIVBoot.exe") == L"CN") {
		normalizedPath = path / L"game";
		return GameReleaseVendor::ShandaGames;
	}

	if (GetPublisherCountryName(path / L"boot" / L"FFXIV_Boot.exe") == L"KR") {
		normalizedPath = path / L"game";
		return GameReleaseVendor::ActozSoft;
	}

	if (GetPublisherCountryName(path / L"boot" / L"FfxivLauncherTC.exe") == L"TW") {
		normalizedPath = path / L"game";
		return GameReleaseVendor::UserjoyGames;
	}

	return GameReleaseVendor::None;
}

std::vector<std::pair<App::GameReleaseVendor, std::filesystem::path>> App::GameInstallationRepository::AutoDetectInstalledGameReleases() {
	std::vector<std::pair<GameReleaseVendor, std::filesystem::path>> res;

	if (const auto reg = ReadRegistryAsString(
		HKEY_LOCAL_MACHINE,
		LR"(SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{2B41E132-07DF-4925-A3D3-F2D1765CCDFE})",
		L"DisplayIcon"
	); !reg.empty()) {
		std::filesystem::path path;
		if (const auto r = DetermineGameRelease(reg, path); r != GameReleaseVendor::None)
			res.emplace_back(std::make_pair(r, std::move(path)));
	}

	for (const auto steamAppId : {
			39210,  // paid
			312060,  // free trial
		}) {
		if (const auto reg = ReadRegistryAsString(
			HKEY_LOCAL_MACHINE,
			std::format(LR"(SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {})", steamAppId).c_str(),
			L"InstallLocation"); !reg.empty()) {
			std::filesystem::path path;
			if (const auto r = DetermineGameRelease(reg, path); r != GameReleaseVendor::None)
				res.emplace_back(std::make_pair(r, std::move(path)));
		}
	}

	if (const auto reg = ReadRegistryAsString(
		HKEY_CLASSES_ROOT,
		LR"(ff14kr\shell\open\command)",
		L""
	); !reg.empty()) {
		int n;
		const auto args = CommandLineToArgvW(reg.c_str(), &n);
		if (args) {
			if (n) {
				std::filesystem::path path;
				if (const auto r = DetermineGameRelease(args[0], path); r != GameReleaseVendor::None)
					res.emplace_back(std::make_pair(r, std::move(path)));
			}
			LocalFree(args);
		}
	}

	if (const auto reg = ReadRegistryAsString(
		HKEY_LOCAL_MACHINE,
		LR"(SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FFXIV)",
		L"DisplayIcon"
	); !reg.empty()) {
		std::filesystem::path path;
		if (const auto r = DetermineGameRelease(reg, path); r != GameReleaseVendor::None)
			res.emplace_back(std::make_pair(r, std::move(path)));
	}

	if (const auto reg = ReadRegistryAsString(
		HKEY_CLASSES_ROOT,
		LR"(com.userjoy.ffxiv\shell\open\command)",
		L""
	); !reg.empty()) {
		int n;
		const auto args = CommandLineToArgvW(reg.c_str(), &n);
		if (args) {
			if (n) {
				std::filesystem::path path;
				if (const auto r = DetermineGameRelease(args[0], path); r != GameReleaseVendor::None)
					res.emplace_back(std::make_pair(r, std::move(path)));
			}
			LocalFree(args);
		}
	}

	return res;
}

void App::GameInstallationRepository::LoadFrom(const FontGeneratorConfig& config) {
}

void App::GameInstallationRepository::SaveTo(FontGeneratorConfig& config) {
}
