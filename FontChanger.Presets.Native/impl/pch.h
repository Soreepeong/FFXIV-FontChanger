#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <algorithm>
#include <charconv>
#include <cmath>
#include <exception>
#include <format>
#include <fstream>
#include <mutex>
#include <numbers>
#include <ranges>
#include <string>
#include <string_view>
#include <thread>

#include <Windows.h>

#include <comdef.h>
#include <dwrite.h>

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_BITMAP_H
#include FT_OUTLINE_H
#include FT_GLYPH_H

#include <nlohmann/json.hpp>

#include "xivres.fontgen/directwrite_fixed_size_font.h"
#include "xivres.fontgen/fontdata_fixed_size_font.h"
#include "xivres.fontgen/fontdata_packer.h"
#include "xivres.fontgen/freetype_fixed_size_font.h"
#include "xivres.fontgen/merged_fixed_size_font.h"
#include "xivres.fontgen/text_measurer.h"
#include "xivres.fontgen/glyph_merging_fixed_size_font.h"
#include "xivres.fontgen/image_fixed_size_font.h"
#include "xivres.fontgen/wrapping_fixed_size_font.h"
#include "xivres/fontdata.h"
#include "xivres/installation.h"
#include "xivres/texture.h"
#include "xivres/texture.mipmap_stream.h"
#include "xivres/util.on_dtor.h"
#include "xivres/util.pixel_formats.h"

#include "FontChanger.Presets/HResult.h"

using FontChanger::SuccessOrThrow;
