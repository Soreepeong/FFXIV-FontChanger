#pragma once

#include <initializer_list>

#include <Windows.h>

namespace FontChanger {
	// Returns hr if it is a success or one of the acceptable values; throws std::runtime_error with the message of the
	// error otherwise.
	HRESULT SuccessOrThrow(HRESULT hr, std::initializer_list<HRESULT> acceptables = {});
}
