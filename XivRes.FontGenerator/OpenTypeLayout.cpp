#include "pch.h"
#include "OpenTypeLayout.h"

#include <bit>

#include <harfbuzz/hb-subset.h>

namespace {
	using App::OpenTypeLayout::Source;

	constexpr uint32_t MakeTag(const char* s) {
		return static_cast<uint32_t>(static_cast<uint8_t>(s[0])) << 24
			| static_cast<uint32_t>(static_cast<uint8_t>(s[1])) << 16
			| static_cast<uint32_t>(static_cast<uint8_t>(s[2])) << 8
			| static_cast<uint32_t>(static_cast<uint8_t>(s[3]));
	}

	constexpr auto TagDflt = MakeTag("DFLT");
	constexpr auto TagLatn = MakeTag("latn");
	constexpr auto TagRclt = MakeTag("rclt");
	constexpr auto TagKern = MakeTag("kern");

	// Scripts that the kerning of glyphs from no source applies in: those that shapers might pick for the text.
	constexpr const char* KerningScripts[]{"DFLT", "cyrl", "grek", "hang", "hani", "kana", "latn", "thai"};

	constexpr uint16_t NoRequiredFeature = 0xFFFF;
	constexpr uint16_t LookupFlagUseMarkFilteringSet = 0x0010;
	constexpr uint16_t GsubExtension = 7;
	constexpr uint16_t GposExtension = 9;

	class Reader {
		std::span<const uint8_t> m_data;

	public:
		explicit Reader(std::span<const uint8_t> data) : m_data(data) {}

		[[nodiscard]] size_t Size() const { return m_data.size(); }

		void Check(size_t offset, size_t length) const {
			if (offset > m_data.size() || length > m_data.size() - offset)
				throw std::runtime_error("A layout table of a font is truncated.");
		}

		[[nodiscard]] uint16_t U16(size_t offset) const {
			Check(offset, 2);
			return static_cast<uint16_t>(m_data[offset] << 8 | m_data[offset + 1]);
		}

		[[nodiscard]] int16_t I16(size_t offset) const { return static_cast<int16_t>(U16(offset)); }

		[[nodiscard]] uint32_t U32(size_t offset) const { return static_cast<uint32_t>(U16(offset)) << 16 | U16(offset + 2); }

		[[nodiscard]] std::vector<uint8_t> Bytes(size_t offset, size_t length) const {
			Check(offset, length);
			return {m_data.begin() + static_cast<ptrdiff_t>(offset), m_data.begin() + static_cast<ptrdiff_t>(offset + length)};
		}
	};

	uint16_t GetU16(const std::vector<uint8_t>& d, size_t at) {
		return static_cast<uint16_t>(d.at(at) << 8 | d.at(at + 1));
	}

	void PutU16(std::vector<uint8_t>& d, size_t at, uint32_t v) {
		d.at(at) = static_cast<uint8_t>(v >> 8);
		d.at(at + 1) = static_cast<uint8_t>(v);
	}

	void PutI16(std::vector<uint8_t>& d, size_t at, float v) {
		PutU16(d, at, static_cast<uint16_t>(static_cast<int16_t>(std::clamp(std::lround(v), -32768l, 32767l))));
	}

	void AppendU16(std::vector<uint8_t>& d, uint32_t v) {
		d.push_back(static_cast<uint8_t>(v >> 8));
		d.push_back(static_cast<uint8_t>(v));
	}

	void AppendU32(std::vector<uint8_t>& d, uint32_t v) {
		AppendU16(d, v >> 16);
		AppendU16(d, v & 0xFFFF);
	}

	// Tables as graphs of objects, which HarfBuzz lays out so that the offsets between them fit.
	struct Link {
		uint32_t Position;
		uint32_t Width;
		size_t Target;
	};

	struct Object {
		std::vector<uint8_t> Data;
		std::vector<Link> Links;
	};

	class Graph {
	public:
		std::vector<Object> Objects;

		size_t Add(std::vector<uint8_t> data, std::vector<Link> links = {}) {
			Objects.push_back({std::move(data), std::move(links)});
			return Objects.size() - 1;
		}

		// Lays out the objects reachable from root into a table; HarfBuzz may change the objects while it does.
		std::vector<uint8_t> Serialize(size_t root, uint32_t tableTag) {
			// Children come before their parents, and the root last.
			std::vector<size_t> order;
			std::vector<int> newIndices(Objects.size(), -1);
			std::function<void(size_t)> visit = [&](size_t index) {
				if (newIndices[index] != -1)
					return;
				newIndices[index] = -2;
				for (const auto& link : Objects[index].Links)
					visit(link.Target);
				newIndices[index] = static_cast<int>(order.size());
				order.push_back(index);
			};
			visit(root);

			// Index 0 is taken by the null object that HarfBuzz adds.
			std::vector<std::vector<hb_subset_serialize_link_t>> links(order.size());
			std::vector<hb_subset_serialize_object_t> objects(order.size());
			for (size_t i = 0; i < order.size(); i++) {
				auto& object = Objects[order[i]];
				for (const auto& link : object.Links)
					links[i].push_back({link.Width, link.Position, static_cast<unsigned>(newIndices[link.Target] + 1)});
				const auto head = reinterpret_cast<char*>(object.Data.data());
				objects[i] = {
					.head = head,
					.tail = head + object.Data.size(),
					.num_real_links = static_cast<unsigned>(links[i].size()),
					.real_links = links[i].data(),
					.num_virtual_links = 0,
					.virtual_links = nullptr,
				};
			}

			const auto blob = hb_subset_serialize_or_fail(tableTag, objects.data(), static_cast<unsigned>(objects.size()));
			if (!blob)
				throw std::runtime_error("The layout tables of the fonts do not fit in an OpenType font.");
			unsigned length;
			const auto data = hb_blob_get_data(blob, &length);
			std::vector<uint8_t> res(data, data + length);
			hb_blob_destroy(blob);
			return res;
		}
	};

	struct ClassRange {
		uint16_t First;
		uint16_t Last;
		uint16_t Class;
	};

	// Reads the glyphs of a class definition that are not in class 0.
	std::vector<ClassRange> ReadClassDef(const Reader& r, size_t offset) {
		std::vector<ClassRange> res;
		switch (r.U16(offset)) {
			case 1: {
				const auto start = r.U16(offset + 2);
				const auto count = r.U16(offset + 4);
				for (uint32_t i = 0; i < count; i++) {
					const auto cls = r.U16(offset + 6 + 2 * i);
					const auto glyph = static_cast<uint16_t>(start + i);
					if (!cls)
						continue;
					if (!res.empty() && res.back().Class == cls && res.back().Last + 1 == glyph)
						res.back().Last = glyph;
					else
						res.push_back({glyph, glyph, cls});
				}
				break;
			}

			case 2: {
				const auto count = r.U16(offset + 2);
				for (size_t i = 0; i < count; i++) {
					const auto at = offset + 4 + 6 * i;
					if (const auto cls = r.U16(at + 4))
						res.push_back({r.U16(at), r.U16(at + 2), cls});
				}
				break;
			}

			default:
				throw std::runtime_error("A layout table of a font has an unknown class definition format.");
		}
		return res;
	}

	size_t GetClassDefSize(const Reader& r, size_t offset) {
		switch (r.U16(offset)) {
			case 1: return 6 + 2 * size_t{r.U16(offset + 4)};
			case 2: return 4 + 6 * size_t{r.U16(offset + 2)};
			default: throw std::runtime_error("A layout table of a font has an unknown class definition format.");
		}
	}

	std::vector<uint8_t> WriteClassDef(std::vector<ClassRange> ranges) {
		std::ranges::sort(ranges, {}, &ClassRange::First);
		std::vector<ClassRange> merged;
		for (const auto& range : ranges) {
			if (!merged.empty() && merged.back().Class == range.Class && merged.back().Last + 1 == range.First)
				merged.back().Last = range.Last;
			else
				merged.push_back(range);
		}

		std::vector<uint8_t> res;
		AppendU16(res, 2);
		AppendU16(res, static_cast<uint32_t>(merged.size()));
		for (const auto& range : merged) {
			AppendU16(res, range.First);
			AppendU16(res, range.Last);
			AppendU16(res, range.Class);
		}
		return res;
	}

	std::vector<uint16_t> ReadCoverage(const Reader& r, size_t offset) {
		std::vector<uint16_t> res;
		switch (r.U16(offset)) {
			case 1:
				for (size_t i = 0, count = r.U16(offset + 2); i < count; i++)
					res.push_back(r.U16(offset + 4 + 2 * i));
				break;

			case 2:
				for (size_t i = 0, count = r.U16(offset + 2); i < count; i++) {
					const auto at = offset + 4 + 6 * i;
					for (uint32_t g = r.U16(at), last = r.U16(at + 2); g <= last; g++)
						res.push_back(static_cast<uint16_t>(g));
				}
				break;

			default:
				throw std::runtime_error("A layout table of a font has an unknown coverage format.");
		}
		return res;
	}

	size_t GetCoverageSize(const Reader& r, size_t offset) {
		switch (r.U16(offset)) {
			case 1: return 4 + 2 * size_t{r.U16(offset + 2)};
			case 2: return 4 + 6 * size_t{r.U16(offset + 2)};
			default: throw std::runtime_error("A layout table of a font has an unknown coverage format.");
		}
	}

	// Writes a coverage of glyphs in ascending order without duplicates, as a list or as ranges, whichever is smaller.
	std::vector<uint8_t> WriteCoverage(const std::vector<uint16_t>& glyphs) {
		std::vector<std::pair<uint16_t, uint16_t>> ranges;
		for (const auto g : glyphs) {
			if (!ranges.empty() && ranges.back().second + 1 == g)
				ranges.back().second = g;
			else
				ranges.emplace_back(g, g);
		}

		std::vector<uint8_t> res;
		if (ranges.size() * 6 < glyphs.size() * 2) {
			AppendU16(res, 2);
			AppendU16(res, static_cast<uint32_t>(ranges.size()));
			uint32_t index = 0;
			for (const auto& [first, last] : ranges) {
				AppendU16(res, first);
				AppendU16(res, last);
				AppendU16(res, index);
				index += last - first + 1u;
			}
		} else {
			AppendU16(res, 1);
			AppendU16(res, static_cast<uint32_t>(glyphs.size()));
			for (const auto g : glyphs)
				AppendU16(res, g);
		}
		return res;
	}

	size_t GetValueRecordSize(uint16_t format) {
		return 2 * static_cast<size_t>(std::popcount(static_cast<unsigned>(format & 0xFF)));
	}

	struct ParsedLangSys {
		uint16_t RequiredFeature = NoRequiredFeature;
		std::vector<uint16_t> Features;
	};

	struct ParsedScript {
		std::optional<ParsedLangSys> Default;
		std::map<uint32_t, ParsedLangSys> LangSys;
	};

	struct ParsedFeature {
		uint32_t Tag = 0;
		std::vector<uint16_t> Lookups;
	};

	struct ParsedLookup {
		uint16_t Type = 0;
		uint16_t Flag = 0;
		uint16_t MarkFilteringSet = 0;
		std::vector<size_t> Subtables;
	};

	struct ParsedTable {
		std::map<uint32_t, ParsedScript> Scripts;
		std::vector<ParsedFeature> Features;
		std::vector<ParsedLookup> Lookups;
	};

	struct SourceContext {
		const Source* Src = nullptr;

		// Index of the first lookup of the source in the merged lookup list.
		uint16_t LookupBase = 0;

		// Glyphs of the merged font that are not of the source, besides .notdef.
		std::vector<std::pair<uint16_t, uint16_t>> ForeignRanges;
	};

	// Reads GSUB or GPOS into objects of a graph, changing them for the merged font as it goes.
	class TableParser {
		enum class Kind {
			Coverage,
			ClassDef,
			Anchor,
			GlyphArray,
			Ligature,
			LigatureSet,
			Rule,
			RuleSet,
			ChainedRule,
			ChainedRuleSet,
			PairSet,
			MarkArray,
			AnchorMatrix,
			LigatureArray,
		};

		Reader m_r;
		Graph& m_g;
		const bool m_gpos;
		const SourceContext& m_ctx;
		std::map<std::pair<Kind, size_t>, size_t> m_memo;

		// Alternates that the user picked, by the lookups of the features.
		std::map<uint16_t, uint16_t> m_alternateIndices;

	public:
		uint16_t MaxContext = 0;

		TableParser(std::span<const uint8_t> data, Graph& graph, bool gpos, const SourceContext& ctx)
			: m_r(data)
			, m_g(graph)
			, m_gpos(gpos)
			, m_ctx(ctx) {}

		ParsedTable Parse() {
			ParsedTable res;
			if (m_r.Size() < 10 || m_r.U16(0) != 1)
				return res;

			if (const size_t scriptList = m_r.U16(4)) {
				for (size_t i = 0, count = m_r.U16(scriptList); i < count; i++) {
					const auto tag = m_r.U32(scriptList + 2 + 6 * i);
					const auto script = scriptList + m_r.U16(scriptList + 6 + 6 * i);
					auto& parsed = res.Scripts[tag];
					if (const auto defaultLangSys = m_r.U16(script))
						parsed.Default = ReadLangSys(script + defaultLangSys);
					for (size_t j = 0, langSysCount = m_r.U16(script + 2); j < langSysCount; j++)
						parsed.LangSys[m_r.U32(script + 4 + 6 * j)] = ReadLangSys(script + m_r.U16(script + 8 + 6 * j));
				}
			}

			if (const size_t featureList = m_r.U16(6)) {
				for (size_t i = 0, count = m_r.U16(featureList); i < count; i++) {
					auto& feature = res.Features.emplace_back();
					feature.Tag = m_r.U32(featureList + 2 + 6 * i);
					const auto at = featureList + m_r.U16(featureList + 6 + 6 * i);
					for (size_t j = 0, lookupCount = m_r.U16(at + 2); j < lookupCount; j++)
						feature.Lookups.push_back(m_r.U16(at + 4 + 2 * j));
				}
			}

			for (const auto& feature : res.Features) {
				if (const auto it = m_ctx.Src->EnabledFeatures.find(feature.Tag); it != m_ctx.Src->EnabledFeatures.end() && it->second > 1) {
					for (const auto lookup : feature.Lookups)
						m_alternateIndices.emplace(lookup, static_cast<uint16_t>((std::min)(it->second - 1, 0xFFFFu)));
				}
			}

			if (const size_t lookupList = m_r.U16(8)) {
				const auto extensionType = m_gpos ? GposExtension : GsubExtension;
				for (size_t i = 0, count = m_r.U16(lookupList); i < count; i++) {
					const auto at = lookupList + m_r.U16(lookupList + 2 + 2 * i);
					auto& lookup = res.Lookups.emplace_back();
					lookup.Type = m_r.U16(at);
					lookup.Flag = m_r.U16(at + 2);
					const auto subtableCount = m_r.U16(at + 4);
					if (lookup.Flag & LookupFlagUseMarkFilteringSet)
						lookup.MarkFilteringSet = m_r.U16(at + 6 + 2 * size_t{subtableCount});

					// Extension subtables are unwrapped; HarfBuzz wraps lookups again where offsets would not fit.
					auto type = lookup.Type;
					for (size_t j = 0; j < subtableCount; j++) {
						auto subtable = at + m_r.U16(at + 6 + 2 * j);
						if (lookup.Type == extensionType) {
							type = m_r.U16(subtable + 2);
							subtable += m_r.U32(subtable + 4);
						}
						if (const auto index = ParseSubtable(type, subtable, static_cast<uint16_t>(i)))
							lookup.Subtables.push_back(*index);
					}
					lookup.Type = type == extensionType ? 1 : type;
				}
			}
			return res;
		}

	private:
		ParsedLangSys ReadLangSys(size_t offset) const {
			ParsedLangSys res;
			res.RequiredFeature = m_r.U16(offset + 2);
			for (size_t i = 0, count = m_r.U16(offset + 4); i < count; i++)
				res.Features.push_back(m_r.U16(offset + 6 + 2 * i));
			return res;
		}

		template<typename TMake>
		size_t Memo(Kind kind, size_t offset, TMake&& make) {
			const auto key = std::make_pair(kind, offset);
			if (const auto it = m_memo.find(key); it != m_memo.end())
				return it->second;
			const auto index = make();
			m_memo.emplace(key, index);
			return index;
		}

		// Links the 16-bit offset at pos of data, relative to base, to the object that make returns for the offset.
		template<typename TMake>
		static void LinkOffset(const std::vector<uint8_t>& data, std::vector<Link>& links, size_t pos, size_t base, TMake&& make) {
			if (const auto offset = GetU16(data, pos))
				links.push_back({static_cast<uint32_t>(pos), 2, make(base + offset)});
		}

		void NoteContext(size_t length) {
			MaxContext = static_cast<uint16_t>((std::max)(static_cast<size_t>(MaxContext), (std::min)(length, size_t{0xFFFF})));
		}

		std::optional<size_t> ParseSubtable(uint16_t type, size_t offset, uint16_t lookupIndex) {
			if (m_gpos) {
				switch (type) {
					case 1: return SinglePos(offset);
					case 2: return PairPos(offset);
					case 3: return CursivePos(offset);
					case 4:
					case 6: return MarkBasePos(offset);
					case 5: return MarkLigPos(offset);
					case 7: return Context(offset);
					case 8: return ChainContext(offset);
					default: return std::nullopt;
				}
			}

			switch (type) {
				case 1: return SingleSubst(offset);
				case 2: return GlyphArraySubst(offset, std::nullopt);
				case 3: {
					const auto it = m_alternateIndices.find(lookupIndex);
					return GlyphArraySubst(offset, it == m_alternateIndices.end() ? std::nullopt : std::make_optional(it->second));
				}
				case 4: return LigatureSubst(offset);
				case 5: return Context(offset);
				case 6: return ChainContext(offset);
				case 8: return ReverseChainSubst(offset);
				default: return std::nullopt;
			}
		}

		size_t Coverage(size_t offset) {
			return Memo(Kind::Coverage, offset, [&] { return m_g.Add(m_r.Bytes(offset, GetCoverageSize(m_r, offset))); });
		}

		size_t ClassDef(size_t offset) {
			return Memo(Kind::ClassDef, offset, [&] { return m_g.Add(m_r.Bytes(offset, GetClassDefSize(m_r, offset))); });
		}

		// Copies a class definition, with the glyphs of other sources in a class of their own.
		size_t ForeignClassDef(size_t offset, uint16_t foreignClass) {
			auto ranges = ReadClassDef(m_r, offset);
			for (const auto& [first, last] : m_ctx.ForeignRanges)
				ranges.push_back({first, last, foreignClass});
			return m_g.Add(WriteClassDef(std::move(ranges)));
		}

		uint16_t GetMaxClass(size_t classDefOffset) const {
			uint16_t res = 0;
			for (const auto& range : ReadClassDef(m_r, classDefOffset))
				res = (std::max)(res, range.Class);
			return res;
		}

		std::pair<float, float> TransformPoint(float x, float y) const {
			const auto& t = m_ctx.Src->Transform;
			return {(t.XX * x + t.XY * y) * t.Scale, t.YY * y * t.Scale};
		}

		// Scales the values of a ValueRecord, and drops its device tables.
		void TransformValueRecord(std::vector<uint8_t>& data, size_t pos, uint16_t format) const {
			const auto& t = m_ctx.Src->Transform;
			float values[8]{};
			size_t positions[8]{};
			for (auto bit = 0; bit < 8; bit++) {
				if (format & (1 << bit)) {
					positions[bit] = pos;
					values[bit] = static_cast<int16_t>(GetU16(data, pos));
					pos += 2;
				}
			}
			if (format & 0x01)
				PutI16(data, positions[0], (t.XX * values[0] + t.XY * values[1]) * t.Scale);
			if (format & 0x02)
				PutI16(data, positions[1], t.YY * values[1] * t.Scale);
			if (format & 0x04)
				PutI16(data, positions[2], t.XX * values[2] * t.Scale);
			if (format & 0x08)
				PutI16(data, positions[3], t.YY * values[3] * t.Scale);
			for (auto bit = 4; bit < 8; bit++) {
				if (format & (1 << bit))
					PutU16(data, positions[bit], 0);
			}
		}

		// Anchors become of format 1: contour points and device tables are dropped.
		size_t Anchor(size_t offset) {
			return Memo(Kind::Anchor, offset, [&] {
				const auto format = m_r.U16(offset);
				if (format < 1 || format > 3)
					throw std::runtime_error("A layout table of a font has an unknown anchor format.");
				const auto [x, y] = TransformPoint(m_r.I16(offset + 2), m_r.I16(offset + 4));
				std::vector<uint8_t> data(6);
				PutU16(data, 0, 1);
				PutI16(data, 2, x);
				PutI16(data, 4, y);
				return m_g.Add(std::move(data));
			});
		}

		// Adds the base of the source to the lookup indices of SequenceLookupRecords.
		void ShiftLookupRecords(std::vector<uint8_t>& data, size_t pos, size_t count) const {
			for (size_t i = 0; i < count; i++) {
				const auto at = pos + 4 * i + 2;
				PutU16(data, at, GetU16(data, at) + m_ctx.LookupBase);
			}
		}

		size_t SingleSubst(size_t offset) {
			const auto format = m_r.U16(offset);
			if (format != 1 && format != 2)
				throw std::runtime_error("A layout table of a font has an unknown subtable format.");
			auto data = m_r.Bytes(offset, format == 1 ? 6 : 6 + 2 * size_t{m_r.U16(offset + 4)});
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			return m_g.Add(std::move(data), std::move(links));
		}

		// Multiple substitution, or alternate substitution with alternateIndex the alternate that the user picked.
		size_t GlyphArraySubst(size_t offset, std::optional<uint16_t> alternateIndex) {
			auto data = m_r.Bytes(offset, 6 + 2 * size_t{m_r.U16(offset + 4)});
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			for (size_t i = 0, count = m_r.U16(offset + 4); i < count; i++) {
				LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) {
					if (!alternateIndex)
						return Memo(Kind::GlyphArray, o, [&] { return m_g.Add(m_r.Bytes(o, 2 + 2 * size_t{m_r.U16(o)})); });

					// The picked alternate becomes the only one; 'rclt' picks the first.
					std::vector<uint8_t> picked;
					if (*alternateIndex < m_r.U16(o)) {
						AppendU16(picked, 1);
						AppendU16(picked, m_r.U16(o + 2 + 2 * size_t{*alternateIndex}));
					} else {
						AppendU16(picked, 0);
					}
					return m_g.Add(std::move(picked));
				});
			}
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t LigatureSubst(size_t offset) {
			auto data = m_r.Bytes(offset, 6 + 2 * size_t{m_r.U16(offset + 4)});
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			for (size_t i = 0, count = m_r.U16(offset + 4); i < count; i++)
				LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) { return LigatureSet(o); });
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t LigatureSet(size_t offset) {
			return Memo(Kind::LigatureSet, offset, [&] {
				auto data = m_r.Bytes(offset, 2 + 2 * size_t{m_r.U16(offset)});
				std::vector<Link> links;
				for (size_t i = 0, count = m_r.U16(offset); i < count; i++) {
					LinkOffset(data, links, 2 + 2 * i, offset, [&](size_t o) {
						return Memo(Kind::Ligature, o, [&] {
							const size_t componentCount = m_r.U16(o + 2);
							NoteContext(componentCount);
							return m_g.Add(m_r.Bytes(o, 4 + 2 * (componentCount ? componentCount - 1 : 0)));
						});
					});
				}
				return m_g.Add(std::move(data), std::move(links));
			});
		}

		// Classes that the rules of a class-based context use, for the backtrack, input, and lookahead sequences.
		struct ClassUsage {
			bool UsesZero[3]{};
			uint16_t MaxClass[3]{};

			void Note(int role, uint16_t cls) {
				if (!cls)
					UsesZero[role] = true;
				MaxClass[role] = (std::max)(MaxClass[role], cls);
			}
		};

		// Scans the rule sets whose offsets are at setsPos of the subtable at offset. The first glyph of the input is
		// not scanned, as the coverage keeps glyphs of other sources from being first.
		ClassUsage ScanClassRules(size_t offset, size_t setsPos, size_t setCount, bool chained) const {
			ClassUsage res;
			if (setCount)
				res.MaxClass[1] = static_cast<uint16_t>(setCount - 1);
			for (size_t i = 0; i < setCount; i++) {
				const auto setOffset = m_r.U16(offset + setsPos + 2 * i);
				if (!setOffset)
					continue;
				const auto set = offset + setOffset;
				for (size_t j = 0, ruleCount = m_r.U16(set); j < ruleCount; j++) {
					auto p = set + m_r.U16(set + 2 + 2 * j);
					if (!chained) {
						const auto glyphCount = m_r.U16(p);
						for (size_t k = 1; k < glyphCount; k++)
							res.Note(1, m_r.U16(p + 4 + 2 * (k - 1)));
						continue;
					}

					const auto backtrackCount = m_r.U16(p);
					for (size_t k = 0; k < backtrackCount; k++)
						res.Note(0, m_r.U16(p + 2 + 2 * k));
					p += 2 + 2 * size_t{backtrackCount};
					const auto inputCount = m_r.U16(p);
					for (size_t k = 1; k < inputCount; k++)
						res.Note(1, m_r.U16(p + 2 + 2 * (k - 1)));
					p += 2 + 2 * size_t{inputCount ? inputCount - 1u : 0u};
					const auto lookaheadCount = m_r.U16(p);
					for (size_t k = 0; k < lookaheadCount; k++)
						res.Note(2, m_r.U16(p + 2 + 2 * k));
				}
			}
			return res;
		}

		// Links a class definition of a context subtable, giving glyphs of other sources a class of their own if rules
		// use class 0, which they would fall in.
		void LinkContextClassDef(const std::vector<uint8_t>& data, std::vector<Link>& links, size_t pos, size_t offset, const ClassUsage& usage, int role) {
			LinkOffset(data, links, pos, offset, [&](size_t o) {
				if (!usage.UsesZero[role])
					return ClassDef(o);
				const auto foreignClass = static_cast<uint32_t>((std::max)(GetMaxClass(o), usage.MaxClass[role])) + 1;
				if (foreignClass > 0xFFFF)
					return ClassDef(o);
				return ForeignClassDef(o, static_cast<uint16_t>(foreignClass));
			});
		}

		size_t Rule(size_t offset, bool chained) {
			return Memo(chained ? Kind::ChainedRule : Kind::Rule, offset, [&] {
				if (!chained) {
					const auto glyphCount = m_r.U16(offset);
					const auto lookupCount = m_r.U16(offset + 2);
					const auto recordsPos = 4 + 2 * size_t{glyphCount ? glyphCount - 1u : 0u};
					auto data = m_r.Bytes(offset, recordsPos + 4 * size_t{lookupCount});
					ShiftLookupRecords(data, recordsPos, lookupCount);
					NoteContext(glyphCount);
					return m_g.Add(std::move(data));
				}

				size_t p = 0;
				const auto backtrackCount = m_r.U16(offset + p);
				p += 2 + 2 * size_t{backtrackCount};
				const auto inputCount = m_r.U16(offset + p);
				p += 2 + 2 * size_t{inputCount ? inputCount - 1u : 0u};
				const auto lookaheadCount = m_r.U16(offset + p);
				p += 2 + 2 * size_t{lookaheadCount};
				const auto lookupCount = m_r.U16(offset + p);
				p += 2;
				auto data = m_r.Bytes(offset, p + 4 * size_t{lookupCount});
				ShiftLookupRecords(data, p, lookupCount);
				NoteContext(size_t{backtrackCount} + inputCount + lookaheadCount);
				return m_g.Add(std::move(data));
			});
		}

		size_t RuleSet(size_t offset, bool chained) {
			return Memo(chained ? Kind::ChainedRuleSet : Kind::RuleSet, offset, [&] {
				auto data = m_r.Bytes(offset, 2 + 2 * size_t{m_r.U16(offset)});
				std::vector<Link> links;
				for (size_t i = 0, count = m_r.U16(offset); i < count; i++)
					LinkOffset(data, links, 2 + 2 * i, offset, [&](size_t o) { return Rule(o, chained); });
				return m_g.Add(std::move(data), std::move(links));
			});
		}

		size_t Context(size_t offset) {
			std::vector<Link> links;
			switch (m_r.U16(offset)) {
				case 1: {
					const auto count = m_r.U16(offset + 4);
					auto data = m_r.Bytes(offset, 6 + 2 * size_t{count});
					LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
					for (size_t i = 0; i < count; i++)
						LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) { return RuleSet(o, false); });
					return m_g.Add(std::move(data), std::move(links));
				}

				case 2: {
					const auto count = m_r.U16(offset + 6);
					auto data = m_r.Bytes(offset, 8 + 2 * size_t{count});
					const auto usage = ScanClassRules(offset, 8, count, false);
					LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
					LinkContextClassDef(data, links, 4, offset, usage, 1);
					for (size_t i = 0; i < count; i++)
						LinkOffset(data, links, 8 + 2 * i, offset, [&](size_t o) { return RuleSet(o, false); });
					return m_g.Add(std::move(data), std::move(links));
				}

				case 3: {
					const auto glyphCount = m_r.U16(offset + 2);
					const auto lookupCount = m_r.U16(offset + 4);
					const auto recordsPos = 6 + 2 * size_t{glyphCount};
					auto data = m_r.Bytes(offset, recordsPos + 4 * size_t{lookupCount});
					for (size_t i = 0; i < glyphCount; i++)
						LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) { return Coverage(o); });
					ShiftLookupRecords(data, recordsPos, lookupCount);
					NoteContext(glyphCount);
					return m_g.Add(std::move(data), std::move(links));
				}

				default:
					throw std::runtime_error("A layout table of a font has an unknown subtable format.");
			}
		}

		size_t ChainContext(size_t offset) {
			std::vector<Link> links;
			switch (m_r.U16(offset)) {
				case 1: {
					const auto count = m_r.U16(offset + 4);
					auto data = m_r.Bytes(offset, 6 + 2 * size_t{count});
					LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
					for (size_t i = 0; i < count; i++)
						LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) { return RuleSet(o, true); });
					return m_g.Add(std::move(data), std::move(links));
				}

				case 2: {
					const auto count = m_r.U16(offset + 10);
					auto data = m_r.Bytes(offset, 12 + 2 * size_t{count});
					const auto usage = ScanClassRules(offset, 12, count, true);
					LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
					LinkContextClassDef(data, links, 4, offset, usage, 0);
					LinkContextClassDef(data, links, 6, offset, usage, 1);
					LinkContextClassDef(data, links, 8, offset, usage, 2);
					for (size_t i = 0; i < count; i++)
						LinkOffset(data, links, 12 + 2 * i, offset, [&](size_t o) { return RuleSet(o, true); });
					return m_g.Add(std::move(data), std::move(links));
				}

				case 3: {
					std::vector<size_t> coveragePositions;
					size_t p = 2;
					size_t contextLength = 0;
					for (auto sequence = 0; sequence < 3; sequence++) {
						const auto count = m_r.U16(offset + p);
						contextLength += count;
						for (size_t i = 0; i < count; i++)
							coveragePositions.push_back(p + 2 + 2 * i);
						p += 2 + 2 * size_t{count};
					}
					const auto lookupCount = m_r.U16(offset + p);
					p += 2;
					auto data = m_r.Bytes(offset, p + 4 * size_t{lookupCount});
					for (const auto pos : coveragePositions)
						LinkOffset(data, links, pos, offset, [&](size_t o) { return Coverage(o); });
					ShiftLookupRecords(data, p, lookupCount);
					NoteContext(contextLength);
					return m_g.Add(std::move(data), std::move(links));
				}

				default:
					throw std::runtime_error("A layout table of a font has an unknown subtable format.");
			}
		}

		size_t ReverseChainSubst(size_t offset) {
			std::vector<size_t> coveragePositions{2};
			size_t p = 4;
			size_t contextLength = 1;
			for (auto sequence = 0; sequence < 2; sequence++) {
				const auto count = m_r.U16(offset + p);
				contextLength += count;
				for (size_t i = 0; i < count; i++)
					coveragePositions.push_back(p + 2 + 2 * i);
				p += 2 + 2 * size_t{count};
			}
			auto data = m_r.Bytes(offset, p + 2 + 2 * size_t{m_r.U16(offset + p)});
			std::vector<Link> links;
			for (const auto pos : coveragePositions)
				LinkOffset(data, links, pos, offset, [&](size_t o) { return Coverage(o); });
			NoteContext(contextLength);
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t SinglePos(size_t offset) {
			const auto format = m_r.U16(offset);
			const auto valueFormat = m_r.U16(offset + 4);
			const auto valueSize = GetValueRecordSize(valueFormat);
			std::vector<uint8_t> data;
			if (format == 1) {
				data = m_r.Bytes(offset, 6 + valueSize);
				TransformValueRecord(data, 6, valueFormat);
			} else if (format == 2) {
				const auto count = m_r.U16(offset + 6);
				data = m_r.Bytes(offset, 8 + count * valueSize);
				for (size_t i = 0; i < count; i++)
					TransformValueRecord(data, 8 + i * valueSize, valueFormat);
			} else {
				throw std::runtime_error("A layout table of a font has an unknown subtable format.");
			}
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t PairPos(size_t offset) {
			NoteContext(2);
			const auto format = m_r.U16(offset);
			const auto valueFormat1 = m_r.U16(offset + 4);
			const auto valueFormat2 = m_r.U16(offset + 6);
			const auto valueSize1 = GetValueRecordSize(valueFormat1);
			const auto valueSize2 = GetValueRecordSize(valueFormat2);
			std::vector<Link> links;

			if (format == 1) {
				const auto count = m_r.U16(offset + 8);
				auto data = m_r.Bytes(offset, 10 + 2 * size_t{count});
				LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
				for (size_t i = 0; i < count; i++) {
					LinkOffset(data, links, 10 + 2 * i, offset, [&](size_t o) {
						return Memo(Kind::PairSet, o, [&] {
							const auto recordSize = 2 + valueSize1 + valueSize2;
							const auto pairCount = m_r.U16(o);
							auto pairSet = m_r.Bytes(o, 2 + pairCount * recordSize);
							for (size_t j = 0; j < pairCount; j++) {
								TransformValueRecord(pairSet, 2 + j * recordSize + 2, valueFormat1);
								TransformValueRecord(pairSet, 2 + j * recordSize + 2 + valueSize1, valueFormat2);
							}
							return m_g.Add(std::move(pairSet));
						});
					});
				}
				return m_g.Add(std::move(data), std::move(links));
			}

			if (format != 2)
				throw std::runtime_error("A layout table of a font has an unknown subtable format.");

			const size_t class1Count = m_r.U16(offset + 12);
			const size_t class2Count = m_r.U16(offset + 14);
			const auto recordSize = valueSize1 + valueSize2;
			auto data = m_r.Bytes(offset, 16 + class1Count * class2Count * recordSize);
			auto zeroColumnUsed = false;
			for (size_t c1 = 0; c1 < class1Count; c1++) {
				for (size_t c2 = 0; c2 < class2Count; c2++) {
					const auto at = 16 + (c1 * class2Count + c2) * recordSize;
					TransformValueRecord(data, at, valueFormat1);
					TransformValueRecord(data, at + valueSize1, valueFormat2);
					if (c2 == 0)
						zeroColumnUsed |= std::any_of(data.begin() + static_cast<ptrdiff_t>(at), data.begin() + static_cast<ptrdiff_t>(at + recordSize), [](uint8_t b) { return b != 0; });
				}
			}

			// Glyphs of other sources would be in class 0 as the second glyph; they get a class of their own without values.
			const auto addForeignClass = zeroColumnUsed && class2Count < 0xFFFF;
			if (addForeignClass) {
				std::vector<uint8_t> widened(data.begin(), data.begin() + 16);
				PutU16(widened, 14, static_cast<uint32_t>(class2Count + 1));
				for (size_t c1 = 0; c1 < class1Count; c1++) {
					const auto row = data.begin() + static_cast<ptrdiff_t>(16 + c1 * class2Count * recordSize);
					widened.insert(widened.end(), row, row + static_cast<ptrdiff_t>(class2Count * recordSize));
					widened.insert(widened.end(), recordSize, 0);
				}
				data = std::move(widened);
			}

			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			LinkOffset(data, links, 8, offset, [&](size_t o) { return ClassDef(o); });
			LinkOffset(data, links, 10, offset, [&](size_t o) {
				return addForeignClass ? ForeignClassDef(o, static_cast<uint16_t>(class2Count)) : ClassDef(o);
			});
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t CursivePos(size_t offset) {
			NoteContext(2);
			const auto count = m_r.U16(offset + 4);
			auto data = m_r.Bytes(offset, 6 + 4 * size_t{count});
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			for (size_t i = 0; i < 2 * size_t{count}; i++)
				LinkOffset(data, links, 6 + 2 * i, offset, [&](size_t o) { return Anchor(o); });
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t MarkArray(size_t offset) {
			return Memo(Kind::MarkArray, offset, [&] {
				const auto count = m_r.U16(offset);
				auto data = m_r.Bytes(offset, 2 + 4 * size_t{count});
				std::vector<Link> links;
				for (size_t i = 0; i < count; i++)
					LinkOffset(data, links, 2 + 4 * i + 2, offset, [&](size_t o) { return Anchor(o); });
				return m_g.Add(std::move(data), std::move(links));
			});
		}

		// BaseArray, Mark2Array, and LigatureAttach: a count, and that many rows of anchors, one per mark class.
		size_t AnchorMatrix(size_t offset, size_t classCount) {
			return Memo(Kind::AnchorMatrix, offset, [&] {
				const auto count = m_r.U16(offset) * classCount;
				auto data = m_r.Bytes(offset, 2 + 2 * count);
				std::vector<Link> links;
				for (size_t i = 0; i < count; i++)
					LinkOffset(data, links, 2 + 2 * i, offset, [&](size_t o) { return Anchor(o); });
				return m_g.Add(std::move(data), std::move(links));
			});
		}

		// Mark-to-base and mark-to-mark attachment.
		size_t MarkBasePos(size_t offset) {
			NoteContext(2);
			auto data = m_r.Bytes(offset, 12);
			const auto classCount = m_r.U16(offset + 6);
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			LinkOffset(data, links, 4, offset, [&](size_t o) { return Coverage(o); });
			LinkOffset(data, links, 8, offset, [&](size_t o) { return MarkArray(o); });
			LinkOffset(data, links, 10, offset, [&](size_t o) { return AnchorMatrix(o, classCount); });
			return m_g.Add(std::move(data), std::move(links));
		}

		size_t MarkLigPos(size_t offset) {
			NoteContext(2);
			auto data = m_r.Bytes(offset, 12);
			const auto classCount = m_r.U16(offset + 6);
			std::vector<Link> links;
			LinkOffset(data, links, 2, offset, [&](size_t o) { return Coverage(o); });
			LinkOffset(data, links, 4, offset, [&](size_t o) { return Coverage(o); });
			LinkOffset(data, links, 8, offset, [&](size_t o) { return MarkArray(o); });
			LinkOffset(data, links, 10, offset, [&](size_t o) {
				return Memo(Kind::LigatureArray, o, [&] {
					auto ligatureArray = m_r.Bytes(o, 2 + 2 * size_t{m_r.U16(o)});
					std::vector<Link> ligatureLinks;
					for (size_t i = 0, count = m_r.U16(o); i < count; i++)
						LinkOffset(ligatureArray, ligatureLinks, 2 + 2 * i, o, [&](size_t o2) { return AnchorMatrix(o2, classCount); });
					return m_g.Add(std::move(ligatureArray), std::move(ligatureLinks));
				});
			});
			return m_g.Add(std::move(data), std::move(links));
		}
	};

	// Returns the largest mark attachment class that the lookups of GSUB or GPOS filter by.
	uint16_t GetMaxMarkAttachmentClass(std::span<const uint8_t> table) {
		const Reader r(table);
		if (r.Size() < 10 || r.U16(0) != 1)
			return 0;
		uint16_t res = 0;
		if (const size_t lookupList = r.U16(8)) {
			for (size_t i = 0, count = r.U16(lookupList); i < count; i++)
				res = (std::max)(res, static_cast<uint16_t>(r.U16(lookupList + r.U16(lookupList + 2 + 2 * i) + 2) >> 8));
		}
		return res;
	}

	struct SourceGdef {
		bool Present = false;
		std::vector<ClassRange> GlyphClasses;
		std::vector<ClassRange> MarkAttachClasses;
		uint16_t MarkAttachClassCount = 0;
		std::vector<std::vector<uint16_t>> MarkGlyphSets;
	};

	SourceGdef ReadGdef(const Source& source) {
		SourceGdef res;
		const Reader r(source.Gdef);
		if (r.Size() >= 12 && r.U16(0) == 1) {
			res.Present = true;
			if (const auto offset = r.U16(4))
				res.GlyphClasses = ReadClassDef(r, offset);
			if (const auto offset = r.U16(10))
				res.MarkAttachClasses = ReadClassDef(r, offset);
			if (r.U16(2) >= 2 && r.Size() >= 14) {
				if (const size_t offset = r.U16(12); offset && r.U16(offset) == 1) {
					for (size_t i = 0, count = r.U16(offset + 2); i < count; i++)
						res.MarkGlyphSets.push_back(ReadCoverage(r, offset + r.U32(offset + 4 + 4 * i)));
				}
			}
		}

		for (const auto& range : res.MarkAttachClasses)
			res.MarkAttachClassCount = (std::max)(res.MarkAttachClassCount, range.Class);
		res.MarkAttachClassCount = (std::max)({res.MarkAttachClassCount, GetMaxMarkAttachmentClass(source.Gsub), GetMaxMarkAttachmentClass(source.Gpos)});
		return res;
	}

	struct MergedLookup {
		uint16_t Type = 0;
		uint16_t Flag = 0;
		uint16_t MarkFilteringSet = 0;
		std::vector<size_t> Subtables;
	};

	struct MergedLangSys {
		std::map<uint32_t, std::set<uint16_t>> Features;
		uint32_t RequiredTag = 0;
		std::set<uint16_t> Required;
	};

	struct MergedScript {
		MergedLangSys Default;
		std::map<uint32_t, MergedLangSys> LangSys;
	};

	// Returns the script that shaping with the font alone would use for a script: the script itself, or else 'DFLT',
	// or else 'latn'.
	const ParsedScript* ResolveScript(const ParsedTable& table, uint32_t scriptTag) {
		for (const auto tag : {scriptTag, TagDflt, TagLatn}) {
			if (const auto it = table.Scripts.find(tag); it != table.Scripts.end())
				return &it->second;
		}
		return nullptr;
	}

	// Returns the language system that shaping with the font alone would use: that of the language of the text if the
	// script has it, or else that of the language that the user picked for the source, or else the default one.
	const ParsedLangSys* ResolveLangSys(const ParsedTable& table, const Source& source, uint32_t scriptTag, std::optional<uint32_t> languageTag) {
		const auto script = ResolveScript(table, scriptTag);
		if (!script)
			return nullptr;
		if (languageTag) {
			if (const auto it = script->LangSys.find(*languageTag); it != script->LangSys.end())
				return &it->second;
		}
		for (const auto tag : source.LanguageTags) {
			if (const auto it = script->LangSys.find(tag); it != script->LangSys.end())
				return &it->second;
		}
		return script->Default ? &*script->Default : nullptr;
	}

	// Kerning pairs as pair adjustment subtables of format 1, each small enough for 16-bit offsets.
	MergedLookup MakeKerningLookup(Graph& g, const std::map<uint16_t, std::map<uint16_t, int>>& pairs) {
		MergedLookup res{.Type = 2};
		for (auto it = pairs.begin(); it != pairs.end();) {
			auto end = it;
			size_t size = 10;
			while (end != pairs.end()) {
				const auto added = 2 + 2 + 2 + 4 * end->second.size();
				if (end != it && size + added + 4 > 0xFF00)
					break;
				size += added;
				++end;
			}

			std::vector<uint16_t> firsts;
			std::vector<uint8_t> data;
			std::vector<Link> links;
			AppendU16(data, 1);
			AppendU16(data, 0);  // coverage
			AppendU16(data, 0x0004);  // XAdvance of the first glyph
			AppendU16(data, 0);
			AppendU16(data, static_cast<uint32_t>(std::distance(it, end)));
			for (; it != end; ++it) {
				firsts.push_back(it->first);
				std::vector<uint8_t> pairSet;
				AppendU16(pairSet, static_cast<uint32_t>(it->second.size()));
				for (const auto& [second, value] : it->second) {
					AppendU16(pairSet, second);
					AppendU16(pairSet, static_cast<uint16_t>(static_cast<int16_t>(std::clamp(value, -32768, 32767))));
				}
				links.push_back({static_cast<uint32_t>(data.size()), 2, g.Add(std::move(pairSet))});
				AppendU16(data, 0);
			}
			links.push_back({2, 2, g.Add(WriteCoverage(firsts))});
			res.Subtables.push_back(g.Add(std::move(data), std::move(links)));
		}
		return res;
	}

	std::vector<uint8_t> SerializeTable(Graph& g, uint32_t tableTag, const std::vector<MergedLookup>& lookups, const std::map<uint32_t, MergedScript>& scripts) {
		std::vector<uint8_t> lookupList;
		std::vector<Link> lookupListLinks;
		AppendU16(lookupList, static_cast<uint32_t>(lookups.size()));
		for (const auto& lookup : lookups) {
			std::vector<uint8_t> data;
			std::vector<Link> links;
			AppendU16(data, lookup.Type);
			AppendU16(data, lookup.Flag);
			AppendU16(data, static_cast<uint32_t>(lookup.Subtables.size()));
			for (const auto subtable : lookup.Subtables) {
				links.push_back({static_cast<uint32_t>(data.size()), 2, subtable});
				AppendU16(data, 0);
			}
			if (lookup.Flag & LookupFlagUseMarkFilteringSet)
				AppendU16(data, lookup.MarkFilteringSet);
			lookupListLinks.push_back({static_cast<uint32_t>(lookupList.size()), 2, g.Add(std::move(data), std::move(links))});
			AppendU16(lookupList, 0);
		}
		const auto lookupListIndex = g.Add(std::move(lookupList), std::move(lookupListLinks));

		// One feature for each tag and set of lookups that a language system uses, in the order of tags.
		std::map<std::pair<uint32_t, std::vector<uint16_t>>, uint16_t> featureIndices;
		const auto collect = [&](const MergedLangSys& langSys) {
			for (const auto& [tag, featureLookups] : langSys.Features)
				featureIndices.emplace(std::make_pair(tag, std::vector(featureLookups.begin(), featureLookups.end())), 0);
			if (!langSys.Required.empty())
				featureIndices.emplace(std::make_pair(langSys.RequiredTag, std::vector(langSys.Required.begin(), langSys.Required.end())), 0);
		};
		for (const auto& script : scripts | std::views::values) {
			collect(script.Default);
			for (const auto& langSys : script.LangSys | std::views::values)
				collect(langSys);
		}
		if (featureIndices.size() > 0xFFFF)
			throw std::runtime_error("The fonts have too many features to merge.");

		std::vector<uint8_t> featureList;
		std::vector<Link> featureListLinks;
		AppendU16(featureList, static_cast<uint32_t>(featureIndices.size()));
		for (auto& [key, index] : featureIndices) {
			index = static_cast<uint16_t>(featureListLinks.size());
			std::vector<uint8_t> data;
			AppendU16(data, 0);  // featureParams
			AppendU16(data, static_cast<uint32_t>(key.second.size()));
			for (const auto lookup : key.second)
				AppendU16(data, lookup);
			AppendU32(featureList, key.first);
			featureListLinks.push_back({static_cast<uint32_t>(featureList.size()), 2, g.Add(std::move(data))});
			AppendU16(featureList, 0);
		}
		const auto featureListIndex = g.Add(std::move(featureList), std::move(featureListLinks));

		const auto makeLangSys = [&](const MergedLangSys& langSys) {
			std::set<uint16_t> indices;
			for (const auto& [tag, featureLookups] : langSys.Features)
				indices.insert(featureIndices.at(std::make_pair(tag, std::vector(featureLookups.begin(), featureLookups.end()))));
			std::vector<uint8_t> data;
			AppendU16(data, 0);  // lookupOrder
			AppendU16(data, langSys.Required.empty() ? NoRequiredFeature : featureIndices.at(std::make_pair(langSys.RequiredTag, std::vector(langSys.Required.begin(), langSys.Required.end()))));
			AppendU16(data, static_cast<uint32_t>(indices.size()));
			for (const auto index : indices)
				AppendU16(data, index);
			return g.Add(std::move(data));
		};

		std::vector<uint8_t> scriptList;
		std::vector<Link> scriptListLinks;
		AppendU16(scriptList, static_cast<uint32_t>(scripts.size()));
		for (const auto& [tag, script] : scripts) {
			std::vector<uint8_t> data;
			std::vector<Link> links{{0, 2, makeLangSys(script.Default)}};
			AppendU16(data, 0);
			AppendU16(data, static_cast<uint32_t>(script.LangSys.size()));
			for (const auto& [langSysTag, langSys] : script.LangSys) {
				AppendU32(data, langSysTag);
				links.push_back({static_cast<uint32_t>(data.size()), 2, makeLangSys(langSys)});
				AppendU16(data, 0);
			}
			AppendU32(scriptList, tag);
			scriptListLinks.push_back({static_cast<uint32_t>(scriptList.size()), 2, g.Add(std::move(data), std::move(links))});
			AppendU16(scriptList, 0);
		}
		const auto scriptListIndex = g.Add(std::move(scriptList), std::move(scriptListLinks));

		std::vector<uint8_t> header;
		AppendU32(header, 0x00010000);
		AppendU16(header, 0);
		AppendU16(header, 0);
		AppendU16(header, 0);
		const auto root = g.Add(std::move(header), {{4, 2, scriptListIndex}, {6, 2, featureListIndex}, {8, 2, lookupListIndex}});
		return g.Serialize(root, tableTag);
	}
}

bool App::OpenTypeLayout::IsDefaultFeature(uint32_t tag) {
	static const auto s_tags = [] {
		std::set<uint32_t> res;
		for (const auto name : {
			"abvf", "abvm", "abvs", "akhn", "blwf", "blwm", "blws", "calt", "ccmp", "cfar", "cjct", "clig", "curs", "dist",
			"fin2", "fin3", "fina", "half", "haln", "init", "isol", "kern", "liga", "ljmo", "locl", "ltra", "ltrm", "mark",
			"med2", "medi", "mkmk", "mset", "nukt", "pref", "pres", "pstf", "psts", "rclt", "rkrf", "rlig", "rphf", "rtla",
			"rtlm", "rvrn", "stch", "tjmo", "vatu", "vert", "vjmo", "vrt2",
		})
			res.insert(MakeTag(name));
		return res;
	}();
	return s_tags.contains(tag);
}

App::OpenTypeLayout::Output App::OpenTypeLayout::Merge(const Input& input) {
	Output out;
	const auto sourceCount = input.Sources.size();

	// Mark attachment classes and mark glyph sets of each source come after those of the sources before it.
	std::vector<SourceGdef> gdefs;
	std::vector<uint16_t> markClassBases;
	std::vector<uint16_t> markSetBases;
	uint32_t markClassCount = 0;
	uint32_t markSetCount = 0;
	for (const auto& source : input.Sources) {
		auto& gdef = gdefs.emplace_back(ReadGdef(source));
		markClassBases.push_back(static_cast<uint16_t>(markClassCount));
		markSetBases.push_back(static_cast<uint16_t>(markSetCount));
		markClassCount += gdef.MarkAttachClassCount;
		markSetCount += static_cast<uint32_t>(gdef.MarkGlyphSets.size());
		if (markClassCount > 0xFF)
			throw std::runtime_error("The fonts have more than 255 mark attachment classes in total.");
		if (markSetCount > 0xFFFF)
			throw std::runtime_error("The fonts have too many mark glyph sets to merge.");
	}

	std::vector<SourceContext> contexts(sourceCount);
	for (size_t i = 0; i < sourceCount; i++) {
		const auto& source = input.Sources[i];
		contexts[i].Src = &source;
		if (source.FirstGlyph > 1)
			contexts[i].ForeignRanges.emplace_back(1, static_cast<uint16_t>(source.FirstGlyph - 1));
		if (source.LastGlyph + 1u < input.GlyphCount)
			contexts[i].ForeignRanges.emplace_back(static_cast<uint16_t>(source.LastGlyph + 1), static_cast<uint16_t>(input.GlyphCount - 1));
	}

	const auto buildTable = [&](bool gpos) -> std::vector<uint8_t> {
		Graph g;
		std::vector<ParsedTable> parsed(sourceCount);
		std::vector<uint16_t> lookupBases(sourceCount);
		std::vector<MergedLookup> lookups;
		for (size_t i = 0; i < sourceCount; i++) {
			if (lookups.size() > 0xFFFF)
				throw std::runtime_error("The fonts have too many lookups to merge.");
			lookupBases[i] = static_cast<uint16_t>(lookups.size());
			contexts[i].LookupBase = lookupBases[i];

			TableParser parser(gpos ? input.Sources[i].Gpos : input.Sources[i].Gsub, g, gpos, contexts[i]);
			parsed[i] = parser.Parse();
			out.MaxContext = (std::max)(out.MaxContext, parser.MaxContext);

			for (const auto& lookup : parsed[i].Lookups) {
				auto& merged = lookups.emplace_back(MergedLookup{lookup.Type, lookup.Flag, lookup.MarkFilteringSet, lookup.Subtables});
				if (const auto markClass = lookup.Flag >> 8)
					merged.Flag = static_cast<uint16_t>((lookup.Flag & 0xFF) | (markClass + markClassBases[i]) << 8);
				if (lookup.Flag & LookupFlagUseMarkFilteringSet)
					merged.MarkFilteringSet = static_cast<uint16_t>(lookup.MarkFilteringSet + markSetBases[i]);
			}
		}

		std::optional<uint16_t> kerningLookup;
		if (gpos && !input.Kerning.empty()) {
			kerningLookup = static_cast<uint16_t>(lookups.size());
			lookups.push_back(MakeKerningLookup(g, input.Kerning));
			out.MaxContext = (std::max)(out.MaxContext, uint16_t{2});
		}
		if (lookups.empty())
			return {};
		if (lookups.size() > 0xFFFF)
			throw std::runtime_error("The fonts have too many lookups to merge.");

		std::set<uint32_t> scriptTags{TagDflt};
		for (const auto& table : parsed)
			for (const auto tag : table.Scripts | std::views::keys)
				scriptTags.insert(tag);
		if (kerningLookup)
			for (const auto name : KerningScripts)
				scriptTags.insert(MakeTag(name));

		const auto fill = [&](MergedLangSys& target, uint32_t scriptTag, std::optional<uint32_t> languageTag) {
			for (size_t i = 0; i < sourceCount; i++) {
				const auto& source = input.Sources[i];
				const auto langSys = ResolveLangSys(parsed[i], source, scriptTag, languageTag);
				if (!langSys)
					continue;

				const auto add = [&](uint16_t featureIndex, std::set<uint16_t>& to) {
					if (featureIndex >= parsed[i].Features.size())
						return;
					const auto& feature = parsed[i].Features[featureIndex];
					for (const auto lookup : feature.Lookups) {
						if (lookup < parsed[i].Lookups.size())
							to.insert(static_cast<uint16_t>(lookupBases[i] + lookup));
					}
				};

				for (const auto featureIndex : langSys->Features) {
					if (featureIndex >= parsed[i].Features.size())
						continue;
					const auto tag = parsed[i].Features[featureIndex].Tag;
					add(featureIndex, target.Features[tag]);

					// Features that the user turned on apply by default.
					if (source.EnabledFeatures.contains(tag) && !IsDefaultFeature(tag))
						add(featureIndex, target.Features[TagRclt]);
				}

				if (langSys->RequiredFeature < parsed[i].Features.size()) {
					if (target.Required.empty())
						target.RequiredTag = parsed[i].Features[langSys->RequiredFeature].Tag;
					add(langSys->RequiredFeature, target.Required);
				}
			}

			if (kerningLookup)
				target.Features[TagKern].insert(*kerningLookup);
			std::erase_if(target.Features, [](const auto& p) { return p.second.empty(); });
		};

		std::map<uint32_t, MergedScript> scripts;
		for (const auto scriptTag : scriptTags) {
			auto& script = scripts[scriptTag];
			fill(script.Default, scriptTag, std::nullopt);

			std::set<uint32_t> languageTags;
			for (const auto& table : parsed) {
				if (const auto resolved = ResolveScript(table, scriptTag))
					for (const auto tag : resolved->LangSys | std::views::keys)
						languageTags.insert(tag);
			}
			for (const auto languageTag : languageTags)
				fill(script.LangSys[languageTag], scriptTag, languageTag);
		}

		return SerializeTable(g, MakeTag(gpos ? "GPOS" : "GSUB"), lookups, scripts);
	};

	out.Gsub = buildTable(false);
	out.Gpos = buildTable(true);
	if (out.Gsub.empty() && out.Gpos.empty())
		return out;
	if (std::ranges::none_of(gdefs, &SourceGdef::Present))
		return out;

	// Classes of glyphs: those of the sources, and for the rest, those given.
	std::map<uint16_t, uint16_t> glyphClasses;
	std::vector<ClassRange> markAttachClasses;
	std::vector<std::vector<uint16_t>> markGlyphSets;
	for (size_t i = 0; i < sourceCount; i++) {
		for (const auto& range : gdefs[i].GlyphClasses)
			for (uint32_t glyph = range.First; glyph <= range.Last; glyph++)
				glyphClasses.emplace(static_cast<uint16_t>(glyph), range.Class);
		for (const auto& range : gdefs[i].MarkAttachClasses)
			markAttachClasses.push_back({range.First, range.Last, static_cast<uint16_t>(range.Class + markClassBases[i])});
		for (const auto& set : gdefs[i].MarkGlyphSets)
			markGlyphSets.push_back(set);
	}
	for (const auto& [glyph, cls] : input.GlyphClasses)
		glyphClasses.emplace(glyph, cls);

	std::vector<ClassRange> glyphClassRanges;
	for (const auto& [glyph, cls] : glyphClasses)
		glyphClassRanges.push_back({glyph, glyph, cls});

	Graph g;
	std::vector<uint8_t> header;
	std::vector<Link> links;
	AppendU16(header, 1);
	AppendU16(header, markGlyphSets.empty() ? 0 : 2);
	if (!glyphClassRanges.empty())
		links.push_back({4, 2, g.Add(WriteClassDef(std::move(glyphClassRanges)))});
	if (!markAttachClasses.empty())
		links.push_back({10, 2, g.Add(WriteClassDef(std::move(markAttachClasses)))});
	if (!markGlyphSets.empty()) {
		std::vector<uint8_t> data;
		std::vector<Link> setLinks;
		AppendU16(data, 1);
		AppendU16(data, static_cast<uint32_t>(markGlyphSets.size()));
		for (auto& set : markGlyphSets) {
			std::ranges::sort(set);
			set.erase(std::ranges::unique(set).begin(), set.end());
			setLinks.push_back({static_cast<uint32_t>(data.size()), 4, g.Add(WriteCoverage(set))});
			AppendU32(data, 0);
		}
		links.push_back({12, 2, g.Add(std::move(data), std::move(setLinks))});
	}
	header.resize(markGlyphSets.empty() ? 12 : 14);
	const auto root = g.Add(std::move(header), std::move(links));
	out.Gdef = g.Serialize(root, MakeTag("GDEF"));
	return out;
}
