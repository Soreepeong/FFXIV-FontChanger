#pragma once

HRESULT SuccessOrThrow(HRESULT hr, std::initializer_list<HRESULT> acceptables = {});

std::wstring_view GetStringResource(UINT id, UINT langId);
std::wstring_view GetStringResource(UINT id);
void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const class WException& e);
void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const class std::system_error& e);
void ShowErrorMessageBox(HWND hParent, UINT preambleStringResID, const class std::exception& e);

std::wstring GetOpenTypeFeatureName(enum DWRITE_FONT_FEATURE_TAG tag);

double GetZoomFromWindow(HWND hWnd);
