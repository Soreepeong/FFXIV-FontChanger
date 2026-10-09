#include "pch.h"
#include "OpenTypeWriter.h"

#include <array>
#include <chrono>

#include <harfbuzz/hb-ot.h>
#include <harfbuzz/hb-subset.h>

#include "xivres.fontgen/outline_clipping.h"

#include "OpenTypeLayout.h"

namespace {
	using xivres::fontgen::glyph_metrics;
	using xivres::fontgen::glyph_outline;
	namespace outline_clipping = xivres::fontgen::outline_clipping;

	// CFF assumes 1000 units per em when the Top DICT has no FontMatrix.
	constexpr int UnitsPerEm = 1000;

	// CFF allows up to 65535 glyphs, including .notdef.
	constexpr size_t MaxGlyphCount = 65535;

	class ByteWriter {
	public:
		std::vector<uint8_t> Data;

		void U8(uint32_t v) { Data.push_back(static_cast<uint8_t>(v)); }
		void U16(uint32_t v) { U8(v >> 8); U8(v); }
		void I16(int v) { U16(static_cast<uint16_t>(static_cast<int16_t>(std::clamp(v, -32768, 32767)))); }
		void U32(uint32_t v) { U16(v >> 16); U16(v); }
		void I64(int64_t v) { U32(static_cast<uint32_t>(static_cast<uint64_t>(v) >> 32)); U32(static_cast<uint32_t>(v)); }
		void Tag(const char* t) { for (auto i = 0; i < 4; i++) U8(t[i]); }
		void Bytes(const std::vector<uint8_t>& v) { Data.insert(Data.end(), v.begin(), v.end()); }

		[[nodiscard]] size_t Size() const { return Data.size(); }

		void PutU16(size_t at, uint32_t v) {
			Data[at] = static_cast<uint8_t>(v >> 8);
			Data[at + 1] = static_cast<uint8_t>(v);
		}

		void PutU32(size_t at, uint32_t v) {
			PutU16(at, v >> 16);
			PutU16(at + 2, v & 0xFFFF);
		}

		void Pad4() {
			while (Data.size() % 4)
				U8(0);
		}
	};

	struct GlyphData {
		std::string Name;
		int Advance = 0;
		std::vector<uint8_t> CharString;

		// Bounds of the outline and its control points in font units; glyphs that draw nothing have none.
		bool HasBounds = false;
		int XMin = 0, YMin = 0, XMax = 0, YMax = 0;

		void Include(int x, int y) {
			if (!HasBounds) {
				HasBounds = true;
				XMin = XMax = x;
				YMin = YMax = y;
			} else {
				XMin = (std::min)(XMin, x), XMax = (std::max)(XMax, x);
				YMin = (std::min)(YMin, y), YMax = (std::max)(YMax, y);
			}
		}
	};

	// Numbers of Type 2 charstrings; integers only, as coordinates are rounded to font units.
	void WriteCharStringNumber(std::vector<uint8_t>& out, int v) {
		if (v >= -107 && v <= 107) {
			out.push_back(static_cast<uint8_t>(v + 139));
		} else if (v >= 108 && v <= 1131) {
			v -= 108;
			out.push_back(static_cast<uint8_t>((v >> 8) + 247));
			out.push_back(static_cast<uint8_t>(v));
		} else if (v >= -1131 && v <= -108) {
			v = -v - 108;
			out.push_back(static_cast<uint8_t>((v >> 8) + 251));
			out.push_back(static_cast<uint8_t>(v));
		} else {
			v = std::clamp(v, -32768, 32767);
			out.push_back(28);
			out.push_back(static_cast<uint8_t>(v >> 8));
			out.push_back(static_cast<uint8_t>(v));
		}
	}

	// Numbers of DICTs; offsets are always written in 5 bytes, so that the size of a DICT does not depend on them.
	void WriteDictNumber(std::vector<uint8_t>& out, int v, bool fixedSize = false) {
		if (!fixedSize && v >= -32768 && v <= 32767) {
			// Encoded as in charstrings, up to 28 and a 16-bit integer.
			WriteCharStringNumber(out, v);
		} else {
			out.push_back(29);
			for (auto shift = 24; shift >= 0; shift -= 8)
				out.push_back(static_cast<uint8_t>(static_cast<uint32_t>(v) >> shift));
		}
	}

	namespace CharStringOp {
		constexpr uint8_t RLineTo = 5;
		constexpr uint8_t RRCurveTo = 8;
		constexpr uint8_t EndChar = 14;
		constexpr uint8_t RMoveTo = 21;
	}

	// Encodes an outline in pixels, in the space of glyph_metrics, as a charstring in font units with y growing upwards.
	void EncodeCharString(const glyph_outline& outline, float scale, float ascent, GlyphData& g) {
		auto& cs = g.CharString;
		WriteCharStringNumber(cs, g.Advance);

		struct FPoint {
			float X, Y;
		};
		const auto toUnits = [&](const glyph_outline::point& p) { return FPoint{p.X * scale, (ascent - p.Y) * scale}; };

		auto curX = 0, curY = 0;
		FPoint cur{};
		auto started = false;
		const auto emit = [&](const FPoint& p) {
			const auto x = static_cast<int>(std::lround(p.X));
			const auto y = static_cast<int>(std::lround(p.Y));
			WriteCharStringNumber(cs, x - curX);
			WriteCharStringNumber(cs, y - curY);
			curX = x, curY = y;
			g.Include(x, y);
		};

		size_t i = 0;
		for (const auto verb : outline.Verbs) {
			switch (verb) {
				case glyph_outline::verb::MoveTo:
					cur = toUnits(outline.Points[i++]);
					emit(cur);
					cs.push_back(CharStringOp::RMoveTo);
					started = true;
					break;

				case glyph_outline::verb::LineTo: {
					const auto p = toUnits(outline.Points[i++]);
					if (!started || (std::lround(p.X) == curX && std::lround(p.Y) == curY)) {
						cur = p;
						break;
					}
					emit(p);
					cs.push_back(CharStringOp::RLineTo);
					cur = p;
					break;
				}

				case glyph_outline::verb::QuadTo: {
					// Quadratic curves are cubic curves with control points 2/3 of the way to the quadratic control point.
					const auto c = toUnits(outline.Points[i]);
					const auto p = toUnits(outline.Points[i + 1]);
					i += 2;
					if (!started)
						break;
					emit({cur.X + (c.X - cur.X) * 2 / 3, cur.Y + (c.Y - cur.Y) * 2 / 3});
					emit({p.X + (c.X - p.X) * 2 / 3, p.Y + (c.Y - p.Y) * 2 / 3});
					emit(p);
					cs.push_back(CharStringOp::RRCurveTo);
					cur = p;
					break;
				}

				case glyph_outline::verb::CubicTo: {
					const auto c1 = toUnits(outline.Points[i]);
					const auto c2 = toUnits(outline.Points[i + 1]);
					const auto p = toUnits(outline.Points[i + 2]);
					i += 3;
					if (!started)
						break;
					emit(c1);
					emit(c2);
					emit(p);
					cs.push_back(CharStringOp::RRCurveTo);
					cur = p;
					break;
				}

				case glyph_outline::verb::Close:
					// Contours of charstrings close by themselves.
					break;
			}
		}
		cs.push_back(CharStringOp::EndChar);
	}

	// Makes the outline of a glyph from the squares of its pixels whose coverage is at least threshold.
	glyph_outline MakePixelOutline(const xivres::fontgen::fixed_size_font& font, char32_t codepoint, const glyph_metrics& gm, uint8_t threshold) {
		if (gm.is_effectively_empty())
			return {};

		const auto width = gm.width(), height = gm.height();
		std::vector<uint8_t> coverage(static_cast<size_t>(width) * height);
		font.draw(codepoint, coverage.data(), 1, -gm.X1, -gm.Y1, width, height, 255, 0, 255, 0);

		threshold = (std::max)(threshold, uint8_t{1});
		Clipper2Lib::Paths64 runs;
		for (auto y = 0; y < height; y++) {
			const auto row = &coverage[static_cast<size_t>(y) * width];
			for (auto x = 0; x < width;) {
				if (row[x] < threshold) {
					x++;
					continue;
				}
				const auto x1 = x;
				while (x < width && row[x] >= threshold)
					x++;
				const int64_t l = gm.X1 + x1, r = gm.X1 + x, t = gm.Y1 + y, b = t + 1;
				runs.push_back({{l, t}, {r, t}, {r, b}, {l, b}});
			}
		}
		return outline_clipping::polygons_to_outline(Clipper2Lib::Union(runs, Clipper2Lib::FillRule::NonZero));
	}

	void WriteIndex(ByteWriter& w, const std::vector<std::vector<uint8_t>>& items) {
		w.U16(static_cast<uint32_t>(items.size()));
		if (items.empty())
			return;

		size_t total = 1;
		for (const auto& item : items)
			total += item.size();
		const auto offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
		w.U8(offSize);

		size_t offset = 1;
		const auto writeOffset = [&](size_t v) {
			for (auto i = offSize - 1; i >= 0; i--)
				w.U8(static_cast<uint32_t>(v >> (i * 8)));
		};
		writeOffset(offset);
		for (const auto& item : items) {
			offset += item.size();
			writeOffset(offset);
		}
		for (const auto& item : items)
			w.Bytes(item);
	}

	std::vector<uint8_t> MakeCff(const std::string& postScriptName, const std::vector<GlyphData>& glyphs, const std::array<int, 4>& bbox) {
		// The names are custom strings, whose string IDs start after the 391 standard strings, so the charset is a single
		// range.
		std::vector<std::vector<uint8_t>> names;
		for (size_t i = 1; i < glyphs.size(); i++)
			names.emplace_back(glyphs[i].Name.begin(), glyphs[i].Name.end());

		ByteWriter charset;
		if (glyphs.size() > 1) {
			charset.U8(2);
			charset.U16(391);
			charset.U16(static_cast<uint32_t>(glyphs.size() - 2));
		} else {
			charset.U8(0);
		}

		std::vector<std::vector<uint8_t>> charStrings;
		charStrings.reserve(glyphs.size());
		for (const auto& g : glyphs)
			charStrings.push_back(g.CharString);

		// Widths are always given in the charstrings, relative to nominalWidthX.
		std::vector<uint8_t> privateDict;
		WriteDictNumber(privateDict, 0);
		privateDict.push_back(20);  // defaultWidthX
		WriteDictNumber(privateDict, 0);
		privateDict.push_back(21);  // nominalWidthX

		const auto makeTopDict = [&](int charsetOffset, int charStringsOffset, int privateOffset) {
			std::vector<uint8_t> d;
			for (const auto v : bbox)
				WriteDictNumber(d, v);
			d.push_back(5);  // FontBBox
			WriteDictNumber(d, charsetOffset, true);
			d.push_back(15);  // charset
			WriteDictNumber(d, charStringsOffset, true);
			d.push_back(17);  // CharStrings
			WriteDictNumber(d, static_cast<int>(privateDict.size()), true);
			WriteDictNumber(d, privateOffset, true);
			d.push_back(18);  // Private
			return d;
		};

		ByteWriter head;
		head.U8(1);
		head.U8(0);
		head.U8(4);
		head.U8(4);
		WriteIndex(head, {std::vector<uint8_t>(postScriptName.begin(), postScriptName.end())});
		const auto topDictIndexOffset = head.Size();
		WriteIndex(head, {makeTopDict(0, 0, 0)});
		const auto topDictIndexSize = head.Size() - topDictIndexOffset;
		WriteIndex(head, names);
		WriteIndex(head, {});  // Global Subr INDEX

		const auto charsetOffset = head.Size();
		const auto charStringsOffset = charsetOffset + charset.Size();
		ByteWriter charStringsIndex;
		WriteIndex(charStringsIndex, charStrings);
		const auto privateOffset = charStringsOffset + charStringsIndex.Size();

		// The Top DICT is written again with the offsets, in the same size.
		ByteWriter topDictIndex;
		WriteIndex(topDictIndex, {makeTopDict(static_cast<int>(charsetOffset), static_cast<int>(charStringsOffset), static_cast<int>(privateOffset))});
		if (topDictIndex.Size() != topDictIndexSize)
			throw std::logic_error("Top DICT changed in size");
		std::ranges::copy(topDictIndex.Data, head.Data.begin() + static_cast<ptrdiff_t>(topDictIndexOffset));

		head.Bytes(charset.Data);
		head.Bytes(charStringsIndex.Data);
		head.Bytes(privateDict);
		return std::move(head.Data);
	}

	// A run of consecutive codepoints mapped to consecutive glyphs.
	struct CmapRun {
		uint32_t Start;
		uint32_t End;
		uint32_t Glyph;
	};

	std::vector<uint8_t> MakeCmap(const std::map<char32_t, uint16_t>& glyphIndices) {
		std::vector<CmapRun> runs;
		for (const auto& [codepoint, glyph] : glyphIndices) {
			const auto c = static_cast<uint32_t>(codepoint);
			if (!runs.empty() && runs.back().End + 1 == c && runs.back().Glyph + (c - runs.back().Start) == glyph)
				runs.back().End = c;
			else
				runs.push_back({c, c, glyph});
		}

		// Format 12 for all codepoints.
		ByteWriter f12;
		f12.U16(12);
		f12.U16(0);
		f12.U32(static_cast<uint32_t>(16 + 12 * runs.size()));
		f12.U32(0);
		f12.U32(static_cast<uint32_t>(runs.size()));
		for (const auto& r : runs) {
			f12.U32(r.Start);
			f12.U32(r.End);
			f12.U32(r.Glyph);
		}

		// Format 4 for the basic multilingual plane, unless it would not fit in 64KB; the last segment maps U+FFFF.
		std::vector<CmapRun> bmpRuns;
		for (const auto& r : runs) {
			if (r.Start >= 0xFFFF)
				break;
			bmpRuns.push_back({r.Start, (std::min)(r.End, 0xFFFEu), r.Glyph});
		}
		const auto segCount = bmpRuns.size() + 1;
		const auto f4Length = 16 + 8 * segCount;
		ByteWriter f4;
		if (f4Length <= 0xFFFF) {
			auto entrySelector = 0;
			while ((2u << entrySelector) <= segCount)
				entrySelector++;
			const auto searchRange = 2u << entrySelector;
			f4.U16(4);
			f4.U16(static_cast<uint32_t>(f4Length));
			f4.U16(0);
			f4.U16(static_cast<uint32_t>(segCount * 2));
			f4.U16(searchRange);
			f4.U16(entrySelector);
			f4.U16(static_cast<uint32_t>(segCount * 2 - searchRange));
			for (const auto& r : bmpRuns)
				f4.U16(r.End);
			f4.U16(0xFFFF);
			f4.U16(0);
			for (const auto& r : bmpRuns)
				f4.U16(r.Start);
			f4.U16(0xFFFF);
			for (const auto& r : bmpRuns)
				f4.U16((r.Glyph - r.Start) & 0xFFFF);
			f4.U16(1);
			for (size_t i = 0; i < segCount; i++)
				f4.U16(0);
		}

		// Unicode BMP and full repertoire, and Windows BMP and full repertoire, in that order.
		ByteWriter w;
		const auto hasF4 = !f4.Data.empty();
		const auto numTables = hasF4 ? 4 : 2;
		w.U16(0);
		w.U16(numTables);
		const auto f4Offset = static_cast<uint32_t>(4 + 8 * numTables);
		const auto f12Offset = f4Offset + static_cast<uint32_t>(f4.Size());
		if (hasF4) {
			w.U16(0), w.U16(3), w.U32(f4Offset);
			w.U16(0), w.U16(4), w.U32(f12Offset);
			w.U16(3), w.U16(1), w.U32(f4Offset);
			w.U16(3), w.U16(10), w.U32(f12Offset);
		} else {
			w.U16(0), w.U16(4), w.U32(f12Offset);
			w.U16(3), w.U16(10), w.U32(f12Offset);
		}
		w.Bytes(f4.Data);
		w.Bytes(f12.Data);
		return std::move(w.Data);
	}

	std::string GetCodepointGlyphName(char32_t codepoint) {
		const auto c = static_cast<uint32_t>(codepoint);
		return c <= 0xFFFF ? std::format("uni{:04X}", c) : std::format("u{:X}", c);
	}

	using HbBlobPtr = std::unique_ptr<hb_blob_t, decltype(&hb_blob_destroy)>;
	using HbFacePtr = std::unique_ptr<hb_face_t, decltype(&hb_face_destroy)>;
	using HbFontPtr = std::unique_ptr<hb_font_t, decltype(&hb_font_destroy)>;
	using HbSubsetInputPtr = std::unique_ptr<hb_subset_input_t, decltype(&hb_subset_input_destroy)>;
	using HbSubsetPlanPtr = std::unique_ptr<hb_subset_plan_t, decltype(&hb_subset_plan_destroy)>;

	constexpr hb_tag_t GsubTag = HB_TAG('G', 'S', 'U', 'B');
	constexpr hb_tag_t GposTag = HB_TAG('G', 'P', 'O', 'S');
	constexpr hb_tag_t GdefTag = HB_TAG('G', 'D', 'E', 'F');

	// Glyphs of the fonts of a merged font that share a layout source: the glyphs of their codepoints in the font file,
	// and the glyphs that the rules of the file reach from them, numbered from FirstGlyph in the written font.
	struct GlyphDomain {
		std::shared_ptr<const App::OpenTypeWriter::LayoutSource> Source;
		int VerticalAdjustment = 0;
		std::vector<char32_t> Codepoints;

		HbFacePtr Face{nullptr, &hb_face_destroy};
		HbSubsetInputPtr SubsetInput{nullptr, &hb_subset_input_destroy};

		// Glyphs of the font file in the order of the written font, and the glyph of the font file of each codepoint.
		std::vector<hb_codepoint_t> Glyphs;
		std::map<char32_t, hb_codepoint_t> CodepointGlyphs;
		uint16_t FirstGlyph = 0;
	};

	// Finds the glyphs of the font file that a domain takes.
	void CollectDomainGlyphs(GlyphDomain& domain) {
		const auto& source = *domain.Source;
		const HbBlobPtr blob(hb_blob_create(reinterpret_cast<const char*>(source.FileData->data()), static_cast<unsigned>(source.FileData->size()), HB_MEMORY_MODE_READONLY, nullptr, nullptr), &hb_blob_destroy);
		domain.Face.reset(hb_face_create(blob.get(), static_cast<unsigned>(source.FaceIndex)));
		const auto face = domain.Face.get();
		const HbFontPtr font(hb_font_create(face), &hb_font_destroy);

		domain.SubsetInput.reset(hb_subset_input_create_or_fail());
		if (!domain.SubsetInput)
			throw std::runtime_error("Failed to subset a font.");
		const auto input = domain.SubsetInput.get();

		const auto unicodes = hb_subset_input_unicode_set(input);
		for (const auto codepoint : domain.Codepoints) {
			const auto it = source.CodepointReplacements.find(codepoint);
			const auto mapped = it == source.CodepointReplacements.end() ? codepoint : it->second;
			if (hb_codepoint_t glyph; hb_font_get_nominal_glyph(font.get(), mapped, &glyph) && glyph) {
				domain.CodepointGlyphs.emplace(codepoint, glyph);
				hb_set_add(unicodes, mapped);
			}
		}

		// The features that subsetting keeps by default are those that shapers may apply, which include those on by default.
		const auto features = hb_subset_input_set(input, HB_SUBSET_SETS_LAYOUT_FEATURE_TAG);
		for (const auto& [tag, value] : source.Features) {
			if (value)
				hb_set_add(features, tag);
			else
				hb_set_del(features, tag);
		}

		// Only the layout tables are kept; the glyphs are drawn by the font instead.
		const auto dropTables = hb_subset_input_set(input, HB_SUBSET_SETS_DROP_TABLE_TAG);
		std::vector<hb_tag_t> tags(hb_face_get_table_tags(face, 0, nullptr, nullptr));
		auto tagCount = static_cast<unsigned>(tags.size());
		hb_face_get_table_tags(face, 0, &tagCount, tags.data());
		for (const auto tag : tags) {
			if (tag != GsubTag && tag != GposTag && tag != GdefTag)
				hb_set_add(dropTables, tag);
		}

		if (hb_ot_var_has_data(face)) {
			hb_subset_input_pin_all_axes_to_default(input, face);
			for (const auto& [tag, value] : source.AxisValues)
				hb_subset_input_pin_axis_location(input, face, tag, value);
		}

		const HbSubsetPlanPtr plan(hb_subset_plan_create_or_fail(face, input), &hb_subset_plan_destroy);
		if (!plan)
			throw std::runtime_error("Failed to subset a font.");
		const auto mapping = hb_subset_plan_old_to_new_glyph_mapping(plan.get());
		auto index = -1;
		hb_codepoint_t oldGlyph, newGlyph;
		while (hb_map_next(mapping, &index, &oldGlyph, &newGlyph)) {
			if (oldGlyph)
				domain.Glyphs.push_back(oldGlyph);
		}
		std::ranges::sort(domain.Glyphs);
	}

	// Subsets the layout tables of the font file of a domain, with the glyphs numbered as in the written font.
	App::OpenTypeLayout::Source SubsetDomainLayout(const GlyphDomain& domain, float fontSize) {
		const auto input = domain.SubsetInput.get();
		const auto mapping = hb_subset_input_old_to_new_glyph_mapping(input);
		hb_map_set(mapping, 0, 0);
		for (size_t i = 0; i < domain.Glyphs.size(); i++)
			hb_map_set(mapping, domain.Glyphs[i], static_cast<hb_codepoint_t>(domain.FirstGlyph + i));

		const HbFacePtr subset(hb_subset_or_fail(domain.Face.get(), input), &hb_face_destroy);
		if (!subset)
			throw std::runtime_error("Failed to subset a font.");

		const auto readTable = [&](hb_tag_t tag) {
			const HbBlobPtr blob(hb_face_reference_table(subset.get(), tag), &hb_blob_destroy);
			unsigned length;
			const auto data = reinterpret_cast<const uint8_t*>(hb_blob_get_data(blob.get(), &length));
			return length ? std::vector<uint8_t>(data, data + length) : std::vector<uint8_t>();
		};

		const auto& source = *domain.Source;
		App::OpenTypeLayout::Source res;
		res.Gsub = readTable(GsubTag);
		res.Gpos = readTable(GposTag);
		res.Gdef = readTable(GdefTag);
		res.FirstGlyph = domain.FirstGlyph;
		res.LastGlyph = static_cast<uint16_t>(domain.FirstGlyph + domain.Glyphs.size() - 1);

		// Font units of the file become pixels of the font, and then font units of the written font, where y grows upwards.
		const auto& m = source.ScreenMatrix;
		res.Transform = {
			.XX = m.M11,
			.XY = -m.M12,
			.YY = m.M22,
			.Scale = source.Font->font_size() / static_cast<float>(hb_face_get_upem(domain.Face.get())) * static_cast<float>(UnitsPerEm) / fontSize,
		};

		for (const auto& [tag, value] : source.Features) {
			if (value)
				res.EnabledFeatures.emplace(tag, value);
		}

		if (!source.Language.empty()) {
			hb_tag_t languageTags[HB_OT_MAX_TAGS_PER_LANGUAGE];
			unsigned scriptCount = 0;
			auto languageCount = static_cast<unsigned>(std::size(languageTags));
			hb_ot_tags_from_script_and_language(HB_SCRIPT_UNKNOWN, hb_language_from_string(source.Language.c_str(), -1), &scriptCount, nullptr, &languageCount, languageTags);
			res.LanguageTags.assign(languageTags, languageTags + languageCount);
		}
		return res;
	}

	std::vector<uint8_t> MakeName(const std::vector<std::pair<uint16_t, std::string>>& names) {
		ByteWriter storage;
		ByteWriter w;
		w.U16(0);
		w.U16(static_cast<uint32_t>(names.size()));
		w.U16(static_cast<uint32_t>(6 + 12 * names.size()));
		for (const auto& [id, value] : names) {
			const auto offset = storage.Size();
			for (const auto ch : xivres::util::unicode::convert<std::wstring>(value))
				storage.U16(static_cast<uint16_t>(ch));
			w.U16(3);
			w.U16(1);
			w.U16(0x409);
			w.U16(id);
			w.U16(static_cast<uint32_t>(storage.Size() - offset));
			w.U16(static_cast<uint32_t>(offset));
		}
		w.Bytes(storage.Data);
		return std::move(w.Data);
	}

	// Keeps the characters that PostScript names allow.
	std::string ToPostScriptName(const std::string& s) {
		std::string res;
		for (const auto c : s) {
			if (c > 32 && c < 127 && std::string_view("[](){}<>/%").find(c) == std::string_view::npos)
				res.push_back(c);
		}
		return res;
	}

	uint32_t GetTableChecksum(const std::vector<uint8_t>& data) {
		uint32_t sum = 0;
		for (size_t i = 0; i < data.size(); i += 4) {
			uint32_t v = 0;
			for (size_t j = 0; j < 4; j++)
				v = v << 8 | (i + j < data.size() ? data[i + j] : 0);
			sum += v;
		}
		return sum;
	}
}

std::vector<uint8_t> App::OpenTypeWriter::Write(const xivres::fontgen::fixed_size_font& font, const Options& options, const std::function<void(size_t, size_t)>& progress) {
	const auto size = font.font_size();
	if (size <= 0)
		throw std::invalid_argument("The font has no size");

	const auto scale = static_cast<float>(UnitsPerEm) / size;
	const auto ascentPx = static_cast<float>(font.ascent());
	const auto toUnits = [scale](float px) { return static_cast<int>(std::lround(px * scale)); };

	// Glyph 0 is .notdef, a hollow box.
	std::vector<GlyphData> glyphs(1);
	{
		auto& notdef = glyphs[0];
		notdef.Advance = UnitsPerEm / 2;
		const auto px = [&](int units) { return static_cast<float>(units) / scale; };
		const auto top = ascentPx - px(UnitsPerEm * 7 / 10);
		glyph_outline box;
		box.move_to({px(50), top});
		box.line_to({px(450), top});
		box.line_to({px(450), ascentPx});
		box.line_to({px(50), ascentPx});
		box.close();
		box.move_to({px(100), top + px(50)});
		box.line_to({px(100), ascentPx - px(50)});
		box.line_to({px(400), ascentPx - px(50)});
		box.line_to({px(400), top + px(50)});
		box.close();
		EncodeCharString(box, scale, ascentPx, notdef);
	}

	// Codepoints of fonts with layout sources go to the domains of the sources; the rest get a glyph each.
	const auto merged = dynamic_cast<const xivres::fontgen::merged_fixed_size_font*>(&font);
	std::vector<char32_t> plainCodepoints;
	std::vector<GlyphDomain> domains;
	{
		std::map<std::string, size_t> domainIndices;
		for (const auto codepoint : font.all_codepoints()) {
			const auto fontIndex = merged ? merged->get_font_index(codepoint) : std::nullopt;
			const auto source = fontIndex && *fontIndex < options.LayoutSources.size() ? options.LayoutSources[*fontIndex] : nullptr;
			if (!source) {
				plainCodepoints.push_back(codepoint);
				continue;
			}

			const auto verticalAdjustment = merged->get_font_vertical_adjustment(*fontIndex);
			const auto [it, added] = domainIndices.emplace(std::format("{}:{}", source->Key, verticalAdjustment), domains.size());
			if (added) {
				auto& domain = domains.emplace_back();
				domain.Source = source;
				domain.VerticalAdjustment = verticalAdjustment;
			}
			domains[it->second].Codepoints.push_back(codepoint);
		}
	}

	size_t glyphCount = 1;
	for (auto& domain : domains) {
		CollectDomainGlyphs(domain);
		for (const auto codepoint : domain.Codepoints) {
			if (!domain.CodepointGlyphs.contains(codepoint))
				plainCodepoints.push_back(codepoint);
		}
		glyphCount += domain.Glyphs.size();
	}
	std::ranges::sort(plainCodepoints);
	glyphCount += plainCodepoints.size();
	if (glyphCount > MaxGlyphCount)
		throw std::runtime_error(std::format("The font has {} glyphs, but OpenType fonts can have up to {}.", glyphCount, MaxGlyphCount));

	const auto tolerance = size / outline_clipping::FlatteningUnitsPerEm;
	const auto progressTotal = glyphCount - 1;
	size_t done = 0;
	const auto reportProgress = [&] {
		if (progress)
			progress(done, progressTotal);
	};

	// Glyphs of the codepoints of the cmap table.
	std::map<char32_t, uint16_t> glyphIndices;

	// Glyphs that are not of domains, which only the kerning pairs of the font apply to.
	std::map<char32_t, uint16_t> plainGlyphIndices;
	for (const auto codepoint : plainCodepoints) {
		reportProgress();
		done++;

		glyph_metrics gm;
		if (!font.try_get_glyph_metrics(codepoint, gm))
			continue;

		glyph_outline outline;
		if (!font.try_get_glyph_outline(codepoint, outline))
			outline = MakePixelOutline(font, codepoint, gm, options.PixelThreshold);
		else if (outline.EvenOdd)
			outline = outline_clipping::polygons_to_outline(outline_clipping::flatten_to_regions(outline, tolerance));

		plainGlyphIndices.emplace(codepoint, static_cast<uint16_t>(glyphs.size()));
		glyphIndices.emplace(codepoint, static_cast<uint16_t>(glyphs.size()));
		auto& g = glyphs.emplace_back();
		g.Name = GetCodepointGlyphName(codepoint);
		g.Advance = (std::max)(0, toUnits(static_cast<float>(gm.AdvanceX)));
		EncodeCharString(outline, scale, ascentPx, g);
	}

	// Glyphs of domains are drawn by their indices, placed as the fonts place the glyphs of codepoints, but without the
	// adjustments of features, which the rules apply instead.
	for (auto& domain : domains) {
		domain.FirstGlyph = static_cast<uint16_t>(glyphs.size());
		const auto& source = *domain.Source;
		const auto& sourceFont = *source.Font;
		const auto originX = static_cast<float>(source.HorizontalOffset);
		const auto originY = static_cast<float>(sourceFont.ascent() + source.BaselineShift + domain.VerticalAdjustment);

		// Glyphs are named after the first codepoint that maps to them.
		std::map<hb_codepoint_t, char32_t> glyphCodepoints;
		for (const auto& [codepoint, glyph] : domain.CodepointGlyphs)
			glyphCodepoints.emplace(glyph, codepoint);

		for (const auto glyph : domain.Glyphs) {
			reportProgress();
			done++;

			glyph_outline outline;
			if (!sourceFont.try_get_glyph_index_outline(glyph, originX, originY, outline))
				outline = {};
			else if (outline.EvenOdd)
				outline = outline_clipping::polygons_to_outline(outline_clipping::flatten_to_regions(outline, tolerance));

			auto& g = glyphs.emplace_back();
			const auto it = glyphCodepoints.find(glyph);
			g.Name = it != glyphCodepoints.end() ? GetCodepointGlyphName(it->second) : std::format("glyph{}", glyphs.size() - 1);
			if (glyph_metrics gm; sourceFont.try_get_glyph_index_metrics(glyph, 0, 0, gm))
				g.Advance = (std::max)(0, toUnits(static_cast<float>(gm.AdvanceX + source.LetterSpacing)));
			EncodeCharString(outline, scale, ascentPx, g);
		}

		for (const auto& [codepoint, glyph] : domain.CodepointGlyphs) {
			const auto index = std::ranges::lower_bound(domain.Glyphs, glyph) - domain.Glyphs.begin();
			glyphIndices.emplace(codepoint, static_cast<uint16_t>(domain.FirstGlyph + index));
		}
	}
	reportProgress();

	OpenTypeLayout::Input layoutInput;
	layoutInput.GlyphCount = static_cast<uint32_t>(glyphs.size());
	for (const auto& domain : domains) {
		if (!domain.Glyphs.empty())
			layoutInput.Sources.push_back(SubsetDomainLayout(domain, size));
	}

	for (const auto& [pair, value] : font.all_kerning_pairs()) {
		const auto l = plainGlyphIndices.find(pair.first), r = plainGlyphIndices.find(pair.second);
		if (l == plainGlyphIndices.end() || r == plainGlyphIndices.end())
			continue;
		if (const auto units = toUnits(static_cast<float>(value)))
			layoutInput.Kerning[l->second][r->second] = units;
	}

	// Classes of glyphs as shapers would guess them without GDEF, for glyphs that the sources do not classify.
	const auto unicodeFuncs = hb_unicode_funcs_get_default();
	for (const auto& [codepoint, glyph] : glyphIndices)
		layoutInput.GlyphClasses.emplace(glyph, hb_unicode_general_category(unicodeFuncs, codepoint) == HB_UNICODE_GENERAL_CATEGORY_NON_SPACING_MARK ? 3 : 1);

	const auto layout = OpenTypeLayout::Merge(layoutInput);

	// Metrics over all glyphs.
	auto xMin = 0, yMin = 0, xMax = 0, yMax = 0;
	auto anyBounds = false;
	auto advanceMax = 0, minLsb = 0, minRsb = 0, xMaxExtent = 0;
	int64_t advanceSum = 0;
	size_t advanceCount = 0;
	for (const auto& g : glyphs) {
		advanceMax = (std::max)(advanceMax, g.Advance);
		if (g.Advance > 0)
			advanceSum += g.Advance, advanceCount++;
		if (!g.HasBounds)
			continue;
		if (!anyBounds) {
			anyBounds = true;
			xMin = g.XMin, yMin = g.YMin, xMax = g.XMax, yMax = g.YMax;
			minLsb = g.XMin, minRsb = g.Advance - g.XMax, xMaxExtent = g.XMax;
		} else {
			xMin = (std::min)(xMin, g.XMin), yMin = (std::min)(yMin, g.YMin);
			xMax = (std::max)(xMax, g.XMax), yMax = (std::max)(yMax, g.YMax);
			minLsb = (std::min)(minLsb, g.XMin);
			minRsb = (std::min)(minRsb, g.Advance - g.XMax);
			xMaxExtent = (std::max)(xMaxExtent, g.XMax);
		}
	}

	const auto ascender = toUnits(ascentPx);
	const auto descender = -toUnits(static_cast<float>(font.line_height()) - ascentPx);
	const auto glyphTop = [&](char32_t c) {
		const auto it = glyphIndices.find(c);
		return it != glyphIndices.end() && glyphs[it->second].HasBounds ? glyphs[it->second].YMax : 0;
	};

	const auto family = options.FamilyName.empty() ? font.family_name() : options.FamilyName;
	const auto subfamily = options.SubfamilyName.empty() ? std::string("Regular") : options.SubfamilyName;
	auto postScriptName = ToPostScriptName(family) + "-" + ToPostScriptName(subfamily);
	if (postScriptName.size() > 63)
		postScriptName.resize(63);

	std::vector<std::pair<std::array<char, 4>, std::vector<uint8_t>>> tables;
	const auto addTable = [&](const char* tag, std::vector<uint8_t> data) {
		tables.emplace_back(std::array{tag[0], tag[1], tag[2], tag[3]}, std::move(data));
	};

	addTable("CFF ", MakeCff(postScriptName, glyphs, {xMin, yMin, xMax, yMax}));
	if (!layout.Gdef.empty())
		addTable("GDEF", layout.Gdef);
	if (!layout.Gpos.empty())
		addTable("GPOS", layout.Gpos);
	if (!layout.Gsub.empty())
		addTable("GSUB", layout.Gsub);

	{
		// Ranges of Unicode and code pages that the font covers, for the bits that applications look at most.
		uint32_t unicodeRange[4]{};
		uint32_t codePageRange[2]{};
		const auto hasAny = [&](char32_t from, char32_t to) {
			const auto it = glyphIndices.lower_bound(from);
			return it != glyphIndices.end() && it->first <= to;
		};
		const auto setUnicodeBit = [&](int bit, char32_t from, char32_t to) {
			if (hasAny(from, to))
				unicodeRange[bit / 32] |= 1u << (bit % 32);
		};
		setUnicodeBit(0, 0x0000, 0x007F);
		setUnicodeBit(1, 0x0080, 0x00FF);
		setUnicodeBit(2, 0x0100, 0x017F);
		setUnicodeBit(7, 0x0370, 0x03FF);
		setUnicodeBit(9, 0x0400, 0x04FF);
		setUnicodeBit(24, 0x0E00, 0x0E7F);
		setUnicodeBit(48, 0x3000, 0x303F);
		setUnicodeBit(49, 0x3040, 0x309F);
		setUnicodeBit(50, 0x30A0, 0x30FF);
		setUnicodeBit(56, 0xAC00, 0xD7AF);
		setUnicodeBit(57, 0x10000, 0x10FFFF);
		setUnicodeBit(59, 0x4E00, 0x9FFF);
		setUnicodeBit(60, 0xE000, 0xF8FF);
		if (hasAny(0x0020, 0x007E))
			codePageRange[0] |= 1u << 0;
		if (hasAny(0x3040, 0x30FF))
			codePageRange[0] |= 1u << 17;
		if (hasAny(0x4E00, 0x9FFF))
			codePageRange[0] |= 1u << 18 | 1u << 20;
		if (hasAny(0xAC00, 0xD7AF))
			codePageRange[0] |= 1u << 19;

		const auto bold = options.WeightClass >= 700;
		ByteWriter w;
		w.U16(4);
		w.I16(advanceCount ? static_cast<int>(advanceSum / static_cast<int64_t>(advanceCount)) : 0);
		w.U16(options.WeightClass);
		w.U16(options.WidthClass);
		w.U16(0);  // fsType: installable
		w.I16(UnitsPerEm * 65 / 100);  // ySubscriptXSize
		w.I16(UnitsPerEm * 60 / 100);  // ySubscriptYSize
		w.I16(0);  // ySubscriptXOffset
		w.I16(UnitsPerEm * 75 / 1000);  // ySubscriptYOffset
		w.I16(UnitsPerEm * 65 / 100);  // ySuperscriptXSize
		w.I16(UnitsPerEm * 60 / 100);  // ySuperscriptYSize
		w.I16(0);  // ySuperscriptXOffset
		w.I16(UnitsPerEm * 35 / 100);  // ySuperscriptYOffset
		w.I16(UnitsPerEm / 20);  // yStrikeoutSize
		w.I16(UnitsPerEm * 26 / 100);  // yStrikeoutPosition
		w.I16(0);  // sFamilyClass
		for (auto i = 0; i < 10; i++)
			w.U8(0);  // panose
		for (const auto v : unicodeRange)
			w.U32(v);
		w.Tag("XIVR");
		w.U16(bold ? 0x0020 | 0x0080 : 0x0040 | 0x0080);  // BOLD or REGULAR, and USE_TYPO_METRICS
		w.U16(glyphIndices.empty() ? 0 : (std::min)(static_cast<uint32_t>(glyphIndices.begin()->first), 0xFFFFu));
		w.U16(glyphIndices.empty() ? 0 : (std::min)(static_cast<uint32_t>(glyphIndices.rbegin()->first), 0xFFFFu));
		w.I16(ascender);
		w.I16(descender);
		w.I16(0);
		w.U16(static_cast<uint32_t>((std::max)({0, ascender, yMax})));
		w.U16(static_cast<uint32_t>((std::max)({0, -descender, -yMin})));
		for (const auto v : codePageRange)
			w.U32(v);
		w.I16(glyphTop(U'x'));
		w.I16(glyphTop(U'H'));
		w.U16(0);
		w.U16(0x20);
		w.U16((std::max)(layout.MaxContext, uint16_t{1}));
		addTable("OS/2", std::move(w.Data));
	}

	addTable("cmap", MakeCmap(glyphIndices));

	const auto now = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count() + 2082844800;
	{
		ByteWriter w;
		w.U32(0x00010000);
		w.U32(0x00010000);
		w.U32(0);  // checkSumAdjustment, set at the end
		w.U32(0x5F0F3CF5);
		w.U16(0x0003);
		w.U16(UnitsPerEm);
		w.I64(now);
		w.I64(now);
		w.I16(xMin);
		w.I16(yMin);
		w.I16(xMax);
		w.I16(yMax);
		w.U16(options.WeightClass >= 700 ? 1 : 0);
		w.U16(8);
		w.I16(2);
		w.I16(0);
		w.I16(0);
		addTable("head", std::move(w.Data));
	}
	{
		ByteWriter w;
		w.U32(0x00010000);
		w.I16(ascender);
		w.I16(descender);
		w.I16(0);
		w.U16(static_cast<uint32_t>(advanceMax));
		w.I16(minLsb);
		w.I16(minRsb);
		w.I16(xMaxExtent);
		w.I16(1);
		w.I16(0);
		w.I16(0);
		for (auto i = 0; i < 4; i++)
			w.I16(0);
		w.I16(0);
		w.U16(static_cast<uint32_t>(glyphs.size()));
		addTable("hhea", std::move(w.Data));
	}
	{
		ByteWriter w;
		for (const auto& g : glyphs) {
			w.U16(static_cast<uint32_t>((std::min)(g.Advance, 0xFFFF)));
			w.I16(g.HasBounds ? g.XMin : 0);
		}
		addTable("hmtx", std::move(w.Data));
	}
	{
		// Vertical metrics: each glyph advances by the line height, from a vertical origin at the ascender. GDI refuses
		// fonts with 'vrt2' and no vertical metrics.
		const auto advanceHeight = (std::max)(0, ascender - descender);
		auto minTsb = 0, minBsb = 0, yMaxExtent = 0;
		auto anyVerticalBounds = false;
		// One long metric, of .notdef, with the advance shared by the glyphs after it, which only have top side bearings.
		ByteWriter vmtx;
		vmtx.U16(static_cast<uint32_t>(advanceHeight));
		for (const auto& g : glyphs) {
			const auto tsb = g.HasBounds ? ascender - g.YMax : 0;
			vmtx.I16(tsb);
			if (!g.HasBounds)
				continue;
			const auto bsb = advanceHeight - tsb - (g.YMax - g.YMin);
			const auto extent = tsb + (g.YMax - g.YMin);
			if (!anyVerticalBounds) {
				anyVerticalBounds = true;
				minTsb = tsb, minBsb = bsb, yMaxExtent = extent;
			} else {
				minTsb = (std::min)(minTsb, tsb);
				minBsb = (std::min)(minBsb, bsb);
				yMaxExtent = (std::max)(yMaxExtent, extent);
			}
		}

		ByteWriter vhea;
		vhea.U32(0x00011000);
		vhea.I16(UnitsPerEm / 2);  // vertTypoAscender
		vhea.I16(-UnitsPerEm / 2);  // vertTypoDescender
		vhea.I16(0);  // vertTypoLineGap
		vhea.U16(static_cast<uint32_t>(advanceHeight));
		vhea.I16(minTsb);
		vhea.I16(minBsb);
		vhea.I16(yMaxExtent);
		vhea.I16(0);  // caretSlopeRise: the caret is horizontal
		vhea.I16(1);  // caretSlopeRun
		vhea.I16(0);
		for (auto i = 0; i < 4; i++)
			vhea.I16(0);
		vhea.I16(0);
		vhea.U16(1);  // numOfLongVerMetrics: all glyphs advance alike
		addTable("vhea", std::move(vhea.Data));
		addTable("vmtx", std::move(vmtx.Data));
	}
	{
		ByteWriter w;
		w.U32(0x00005000);
		w.U16(static_cast<uint32_t>(glyphs.size()));
		addTable("maxp", std::move(w.Data));
	}
	{
		const auto fullName = subfamily == "Regular" ? family : family + " " + subfamily;
		addTable("name", MakeName({
			{1, family},
			{2, subfamily},
			{3, std::format("{};{}", postScriptName, options.Version)},
			{4, fullName},
			{5, options.Version},
			{6, postScriptName},
		}));
	}
	{
		ByteWriter w;
		w.U32(0x00030000);
		w.U32(0);
		w.I16(-UnitsPerEm / 10);
		w.I16(UnitsPerEm / 20);
		w.U32(0);
		for (auto i = 0; i < 4; i++)
			w.U32(0);
		addTable("post", std::move(w.Data));
	}

	std::ranges::sort(tables, {}, [](const auto& t) { return std::string_view(t.first.data(), 4); });

	ByteWriter file;
	const auto numTables = static_cast<uint32_t>(tables.size());
	auto entrySelector = 0u;
	while ((2u << entrySelector) <= numTables)
		entrySelector++;
	const auto searchRange = 16u << entrySelector;
	file.Tag("OTTO");
	file.U16(numTables);
	file.U16(searchRange);
	file.U16(entrySelector);
	file.U16(numTables * 16 - searchRange);

	auto offset = static_cast<uint32_t>(12 + 16 * tables.size());
	size_t headOffset = 0;
	for (const auto& [tag, data] : tables) {
		file.Tag(tag.data());
		file.U32(GetTableChecksum(data));
		file.U32(offset);
		file.U32(static_cast<uint32_t>(data.size()));
		if (std::string_view(tag.data(), 4) == "head")
			headOffset = offset;
		offset += static_cast<uint32_t>((data.size() + 3) / 4 * 4);
	}
	for (const auto& t : tables) {
		file.Bytes(t.second);
		file.Pad4();
	}
	file.PutU32(headOffset + 8, 0xB1B0AFBA - GetTableChecksum(file.Data));
	return std::move(file.Data);
}
