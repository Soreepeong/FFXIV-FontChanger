﻿#include "pch.h"
#include "MiscUtil.h"

#include "resource.h"

HRESULT SuccessOrThrow(HRESULT hr, std::initializer_list<HRESULT> acceptables) {
	if (SUCCEEDED(hr))
		return hr;

	for (const auto& h : acceptables) {
		if (h == hr)
			return hr;
	}

	const auto err = _com_error(hr);
	wchar_t* pszMsg = nullptr;
	FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER |
		FORMAT_MESSAGE_FROM_SYSTEM |
		FORMAT_MESSAGE_IGNORE_INSERTS,
		nullptr,
		hr,
		MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US),
		reinterpret_cast<LPWSTR>(&pszMsg),
		0,
		NULL);
	if (pszMsg) {
		std::unique_ptr<wchar_t, decltype(&LocalFree)> pszMsgFree(pszMsg, &LocalFree);

		throw std::runtime_error(std::format(
			"Error (HRESULT=0x{:08X}): {}",
			static_cast<uint32_t>(hr),
			xivres::util::unicode::convert<std::string>(std::wstring(pszMsg))
		));
	} else {
		throw std::runtime_error(std::format(
			"Error (HRESULT=0x{:08X})",
			static_cast<uint32_t>(hr),
			xivres::util::unicode::convert<std::string>(std::wstring(pszMsg))
		));
	}
}

std::wstring_view GetStringResource(UINT uId, UINT langId) {
	// Convert the string ID into a bundle number
	LPCWSTR pwsz = NULL;
	const auto hrsrc = FindResourceExW(g_hInstance, RT_STRING, MAKEINTRESOURCEW(uId / 16 + 1), langId);
	if (hrsrc) {
		const auto hglob = LoadResource(g_hInstance, hrsrc);
		if (hglob) {
			pwsz = static_cast<LPCWSTR>(LockResource(hglob));
			if (pwsz) {
				// okay now walk the string table
				for (int i = uId & 15; i > 0; --i)
					pwsz += 1 + static_cast<UINT>(*pwsz);
				UnlockResource(pwsz);
			}
			FreeResource(hglob);
		}
	}
	return pwsz ? std::wstring_view{ pwsz + 1, static_cast<size_t>(*pwsz) } : std::wstring_view{};
}

std::wstring_view GetStringResource(UINT id) {
	if (const auto res = GetStringResource(id, g_langId); !res.empty())
		return res;

	LPWSTR lpBuffer;
	const auto length = LoadStringW(g_hInstance, id, reinterpret_cast<LPWSTR>(&lpBuffer), 0);
	return {lpBuffer, static_cast<size_t>(length)};
}

static std::wstring OemCpToWString(std::string_view sv) {
	std::wstring buf(MultiByteToWideChar(CP_OEMCP, 0, sv.data(), static_cast<int>(sv.size()), nullptr, 0), L'\0');
	MultiByteToWideChar(CP_OEMCP, 0, sv.data(), static_cast<int>(sv.size()), buf.data(), static_cast<int>(buf.size()));
	return buf;
}

void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const WException& e) {
	MessageBoxW(
		hParent,
		std::format(
			L"{}\n\n{}",
			GetStringResource(preambleStringResID),
			e.what()).c_str(),
		hParent
		? GetWindowString(hParent).c_str()
		: std::wstring(GetStringResource(IDS_APP)).c_str(),
		MB_OK | MB_ICONERROR);
}

void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const std::system_error& e) {
	std::wstring errorText;
	if (e.code().category() != std::system_category()) {
		LPWSTR pErrorText = nullptr;
		FormatMessageW(
			FORMAT_MESSAGE_FROM_SYSTEM
			| FORMAT_MESSAGE_ALLOCATE_BUFFER
			| FORMAT_MESSAGE_IGNORE_INSERTS,
			nullptr,
			e.code().value(),
			g_langId,
			reinterpret_cast<LPWSTR>(&pErrorText), // output 
			0, // minimum size for output buffer
			nullptr);  // arguments - see note 
		if (nullptr != pErrorText) {
			errorText = pErrorText;
			LocalFree(pErrorText);
		}
	}
	MessageBoxW(
		hParent,
		std::format(
			L"{}\n\n{}",
			GetStringResource(preambleStringResID),
			errorText.empty() ? OemCpToWString(e.what()) : errorText).c_str(),
		hParent
		? GetWindowString(hParent).c_str()
		: std::wstring(GetStringResource(IDS_APP)).c_str(),
		MB_OK | MB_ICONERROR);
}

void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const std::exception& e) {
	MessageBoxW(
		hParent,
		std::format(
			L"{}\n\n{}",
			GetStringResource(preambleStringResID),
			OemCpToWString(e.what())).c_str(),
		hParent
		? GetWindowString(hParent).c_str()
		: std::wstring(GetStringResource(IDS_APP)).c_str(),
		MB_OK | MB_ICONERROR);
}

std::wstring GetOpenTypeFeatureName(DWRITE_FONT_FEATURE_TAG tag) {
	const auto c1 = reinterpret_cast<char*>(&tag)[0];
	const auto c2 = reinterpret_cast<char*>(&tag)[1];
	const auto c3 = reinterpret_cast<char*>(&tag)[2];
	const auto c4 = reinterpret_cast<char*>(&tag)[3];
	const auto ssnb = (c3 - '0') * 10 + c4 - '0';
	if (c1 == 'c'
		&& c2 == 'v'
		&& '0' <= c3 && c3 <= '9'
		&& '0' <= c4 && c4 <= '9') {
		return std::vformat(GetStringResource(IDS_OPENTYPEFEATURE_CV00), std::make_wformat_args(ssnb));
	}

	if (c1 == 's'
		&& c2 == 's'
		&& '0' <= c3 && c3 <= '9'
		&& '0' <= c4 && c4 <= '9') {
		return std::vformat(GetStringResource(IDS_OPENTYPEFEATURE_SS00), std::make_wformat_args(ssnb));
	}

	switch (tag) {
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_AALT));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'b', 'v', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ABVF));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'b', 'v', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ABVM));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'b', 'v', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ABVS));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'f', 'r', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_AFRC));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'k', 'h', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_AKHN));
		case DWRITE_MAKE_OPENTYPE_TAG('a', 'p', 'k', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_APKN));
		case DWRITE_MAKE_OPENTYPE_TAG('b', 'l', 'w', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_BLWF));
		case DWRITE_MAKE_OPENTYPE_TAG('b', 'l', 'w', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_BLWM));
		case DWRITE_MAKE_OPENTYPE_TAG('b', 'l', 'w', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_BLWS));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CALT));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'a', 's', 'e'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CASE));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'c', 'm', 'p'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CCMP));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'f', 'a', 'r'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CFAR));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'h', 'w', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CHWS));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'j', 'c', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CJCT));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'l', 'i', 'g'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CLIG));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'p', 'c', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CPCT));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'p', 's', 'p'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CPSP));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 's', 'w', 'h'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CSWH));
		case DWRITE_MAKE_OPENTYPE_TAG('c', 'u', 'r', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_CURS));
		case DWRITE_MAKE_OPENTYPE_TAG('c', '2', 'p', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_C2PC));
		case DWRITE_MAKE_OPENTYPE_TAG('c', '2', 's', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_C2SC));
		case DWRITE_MAKE_OPENTYPE_TAG('d', 'i', 's', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_DIST));
		case DWRITE_MAKE_OPENTYPE_TAG('d', 'l', 'i', 'g'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_DLIG));
		case DWRITE_MAKE_OPENTYPE_TAG('d', 'n', 'o', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_DNOM));
		case DWRITE_MAKE_OPENTYPE_TAG('d', 't', 'l', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_DTLS));
		case DWRITE_MAKE_OPENTYPE_TAG('e', 'x', 'p', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_EXPT));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FALT));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'i', 'n', '2'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FIN2));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'i', 'n', '3'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FIN3));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'i', 'n', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FINA));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'l', 'a', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FLAC));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'r', 'a', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FRAC));
		case DWRITE_MAKE_OPENTYPE_TAG('f', 'w', 'i', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_FWID));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'a', 'l', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HALF));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'a', 'l', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HALN));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HALT));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'i', 's', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HIST));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'k', 'n', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HKNA));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'l', 'i', 'g'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HLIG));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'n', 'g', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HNGL));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'o', 'j', 'o'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HOJO));
		case DWRITE_MAKE_OPENTYPE_TAG('h', 'w', 'i', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_HWID));
		case DWRITE_MAKE_OPENTYPE_TAG('i', 'n', 'i', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_INIT));
		case DWRITE_MAKE_OPENTYPE_TAG('i', 's', 'o', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ISOL));
		case DWRITE_MAKE_OPENTYPE_TAG('i', 't', 'a', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ITAL));
		case DWRITE_MAKE_OPENTYPE_TAG('j', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_JALT));
		case DWRITE_MAKE_OPENTYPE_TAG('j', 'p', '7', '8'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_JP78));
		case DWRITE_MAKE_OPENTYPE_TAG('j', 'p', '8', '3'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_JP83));
		case DWRITE_MAKE_OPENTYPE_TAG('j', 'p', '9', '0'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_JP90));
		case DWRITE_MAKE_OPENTYPE_TAG('j', 'p', '0', '4'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_JP04));
		case DWRITE_MAKE_OPENTYPE_TAG('k', 'e', 'r', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_KERN));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 'f', 'b', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LFBD));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 'i', 'g', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LIGA));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 'j', 'm', 'o'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LJMO));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 'n', 'u', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LNUM));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 'o', 'c', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LOCL));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 't', 'r', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LTRA));
		case DWRITE_MAKE_OPENTYPE_TAG('l', 't', 'r', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_LTRM));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 'a', 'r', 'k'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MARK));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 'e', 'd', '2'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MED2));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 'e', 'd', 'i'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MEDI));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 'g', 'r', 'k'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MGRK));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 'k', 'm', 'k'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MKMK));
		case DWRITE_MAKE_OPENTYPE_TAG('m', 's', 'e', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_MSET));
		case DWRITE_MAKE_OPENTYPE_TAG('n', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_NALT));
		case DWRITE_MAKE_OPENTYPE_TAG('n', 'l', 'c', 'k'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_NLCK));
		case DWRITE_MAKE_OPENTYPE_TAG('n', 'u', 'k', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_NUKT));
		case DWRITE_MAKE_OPENTYPE_TAG('n', 'u', 'm', 'r'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_NUMR));
		case DWRITE_MAKE_OPENTYPE_TAG('o', 'n', 'u', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ONUM));
		case DWRITE_MAKE_OPENTYPE_TAG('o', 'p', 'b', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_OPBD));
		case DWRITE_MAKE_OPENTYPE_TAG('o', 'r', 'd', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ORDN));
		case DWRITE_MAKE_OPENTYPE_TAG('o', 'r', 'n', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ORNM));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PALT));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'c', 'a', 'p'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PCAP));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'k', 'n', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PKNA));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'n', 'u', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PNUM));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'r', 'e', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PREF));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'r', 'e', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PRES));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 's', 't', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PSTF));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 's', 't', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PSTS));
		case DWRITE_MAKE_OPENTYPE_TAG('p', 'w', 'i', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_PWID));
		case DWRITE_MAKE_OPENTYPE_TAG('q', 'w', 'i', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_QWID));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'a', 'n', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RAND));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'c', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RCLT));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'k', 'r', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RKRF));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'l', 'i', 'g'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RLIG));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'p', 'h', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RPHF));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 't', 'b', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RTBD));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 't', 'l', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RTLA));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 't', 'l', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RTLM));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'u', 'b', 'y'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RUBY));
		case DWRITE_MAKE_OPENTYPE_TAG('r', 'v', 'r', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_RVRN));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SALT));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'i', 'n', 'f'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SINF));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'i', 'z', 'e'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SIZE));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'm', 'c', 'p'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SMCP));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'm', 'p', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SMPL));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 's', 't', 'y'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SSTY));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 't', 'c', 'h'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_STCH));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'u', 'b', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SUBS));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'u', 'p', 's'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SUPS));
		case DWRITE_MAKE_OPENTYPE_TAG('s', 'w', 's', 'h'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_SWSH));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'i', 't', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TITL));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'j', 'm', 'o'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TJMO));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'n', 'a', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TNAM));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'n', 'u', 'm'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TNUM));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'r', 'a', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TRAD));
		case DWRITE_MAKE_OPENTYPE_TAG('t', 'w', 'i', 'd'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_TWID));
		case DWRITE_MAKE_OPENTYPE_TAG('u', 'n', 'i', 'c'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_UNIC));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'a', 'l', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VALT));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'a', 'p', 'k'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VAPK));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'a', 't', 'u'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VATU));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'c', 'h', 'w'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VCHW));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'e', 'r', 't'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VERT));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'h', 'a', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VHAL));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'j', 'm', 'o'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VJMO));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'k', 'n', 'a'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VKNA));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'k', 'r', 'n'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VKRN));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'p', 'a', 'l'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VPAL));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'r', 't', '2'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VRT2));
		case DWRITE_MAKE_OPENTYPE_TAG('v', 'r', 't', 'r'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_VRTR));
		case DWRITE_MAKE_OPENTYPE_TAG('z', 'e', 'r', 'o'): return std::wstring(GetStringResource(IDS_OPENTYPEFEATURE_ZERO));
	}

	return L"(Unknown)";
}
