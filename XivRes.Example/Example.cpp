#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <iostream>
#include <Windows.h>
#include <windowsx.h>

#include "XivRes/GameReader.h"
#include "XivRes/PackedFileUnpackingStream.h"
#include "XivRes/TextureStream.h"
#include "XivRes/TexturePackedFileStream.h"
#include "XivRes/Internal/TexturePreview.Windows.h"

template<typename TPassthroughPacker, typename TCompressingPacker>
std::string Test(std::shared_ptr<XivRes::PackedFileStream> packed, XivRes::SqpackPathSpec pathSpec) {
	using namespace XivRes;

	std::shared_ptr<IStream> decoded;

	try {
		decoded = std::make_shared<PackedFileUnpackingStream>(packed);
		const auto p1 = ReadStreamIntoVector<char>(*packed);

		if (decoded->StreamSize() == 0)
			return {};

		const auto orig = ReadStreamIntoVector<char>(*decoded);
		packed = std::make_shared<CompressingPackedFileStream<TCompressingPacker>>(pathSpec, decoded, Z_NO_COMPRESSION);
		auto pac1 = ReadStreamIntoVector<char>(*packed);
		decoded = std::make_shared<PackedFileUnpackingStream>(packed);
		ReadStreamIntoVector<char>(*decoded);

		packed = std::make_shared<PassthroughPackedFileStream<TPassthroughPacker>>(pathSpec, decoded);
		auto pac2 = ReadStreamIntoVector<char>(*packed);
		decoded = std::make_shared<PackedFileUnpackingStream>(packed);
		ReadStreamIntoVector<char>(*decoded);

		for (auto i = Z_NO_COMPRESSION; i <= Z_BEST_COMPRESSION; i++) {
			packed = std::make_shared<CompressingPackedFileStream<TCompressingPacker>>(pathSpec, decoded, i);
			ReadStreamIntoVector<char>(*packed);
			decoded = std::make_shared<PackedFileUnpackingStream>(packed);
			ReadStreamIntoVector<char>(*decoded);
		}

		packed = std::make_shared<PassthroughPackedFileStream<TPassthroughPacker>>(pathSpec, decoded);
		decoded = std::make_shared<PackedFileUnpackingStream>(packed);

		const auto test = ReadStreamIntoVector<char>(*decoded);

		if (test.size() == orig.size() && memcmp(&test[0], &orig[0], orig.size()) == 0)
			return {};
		return "DIFF";
	} catch (const std::exception& e) {
		return e.what();
	}
}

int main() {
	using namespace XivRes;
	system("chcp 65001 > NUL");

	GameReader gameReader(R"(C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game)");

	const auto& packfile = gameReader.GetSqpackReader(0x040000);

	Internal::ThreadPool<SqpackPathSpec, std::string> pool;
	for (const auto& entry : packfile.EntryInfo) {
		const auto cb = [&packfile, pathSpec = entry.PathSpec]()->std::string {
			try {
				auto packed = packfile.GetPackedFileStream(pathSpec);
				switch (packed->GetPackedFileType()) {
					case XivRes::PackedFileType::None:
						break;
					case XivRes::PackedFileType::EmptyOrObfuscated:
						break;
					case XivRes::PackedFileType::Binary:
						return Test<BinaryPassthroughPacker, BinaryCompressingPacker>(std::move(packed), pathSpec);
					case XivRes::PackedFileType::Model:
						break;
					case XivRes::PackedFileType::Texture:
						return Test<TexturePassthroughPacker, TextureCompressingPacker>(std::move(packed), pathSpec);
					default:
						break;
				}

				return {};

			} catch (const std::out_of_range&) {
				return {};
			}
		};

		pool.Submit(entry.PathSpec, cb);
	}

	pool.SubmitDone();

	for (size_t i = 0;; i++) {
		const auto res = pool.GetResult();
		if (!res)
			break;

		const auto& pathSpec = res->first;

		if (!res->second.empty())
			std::cout << std::format("\r[{:0>5}/{:0>5}] {} path={:08x}, name={:08x}, full={:08x}\n", i, packfile.EntryInfo.size(), res->second, pathSpec.PathHash(), pathSpec.NameHash(), pathSpec.FullPathHash());
		else if (i % 1 == 0) {
			std::cout << std::format("\r[{:0>5}/{:0>5}] path={:08x}, name={:08x}, full={:08x}", i, packfile.EntryInfo.size(), pathSpec.PathHash(), pathSpec.NameHash(), pathSpec.FullPathHash());
			std::cout.flush();
		}
	}

	std::cout << std::endl;

	// const auto pathSpec = SqpackPathSpec("common/font/font1.tex");
	// const auto pathSpec = SqpackPathSpec("common/graphics/texture/-caustics.tex");
	// const auto pathSpec = SqpackPathSpec("common/graphics/texture/-omni_shadow_index_table.tex");

	// Internal::ShowTextureStream(TextureStream(decoded));

	return 0;
}
