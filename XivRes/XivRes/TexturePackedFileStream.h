#ifndef _XIVRES_TEXTUREPACKEDFILESTREAM_H_
#define _XIVRES_TEXTUREPACKEDFILESTREAM_H_

#include "Internal/SpanCast.h"
#include "Internal/ZlibWrapper.h"

#include "LazyPackedFileStream.h"
#include "Texture.h"

namespace XivRes {
	class TexturePassthroughPacker : public AbstractPassthroughFilePacker<PackedFileType::Texture> {
		static constexpr auto MaxMipmapCountPerTexture = 16;

		std::mutex m_mtx;

		std::vector<uint8_t> m_mergedHeader;
		std::vector<PackedLodBlockLocator> m_blockLocators;
		std::vector<uint32_t> m_mipmapOffsetsWithRepeats;
		std::vector<uint32_t> m_mipmapSizes;

	public:
		using AbstractPassthroughFilePacker<PackedFileType::Texture>::AbstractPassthroughFilePacker;

		[[nodiscard]] std::streamsize StreamSize() override {
			const auto blockCount = MaxMipmapCountPerTexture + Align<uint64_t>(m_stream->StreamSize(), EntryBlockDataSize).Count;

			std::streamsize size = 0;

			// PackedFileHeader packedFileHeader;
			size += sizeof PackedFileHeader;

			// PackedLodBlockLocator lodBlocks[mipmapCount];
			size += MaxMipmapCountPerTexture * sizeof PackedLodBlockLocator;

			// uint16_t subBlockSizes[blockCount];
			size += blockCount * sizeof uint16_t;

			// Align block
			size = Align(size);

			// TextureHeader textureHeader;
			size += sizeof TextureHeader;

			// Mipmap offsets
			size += blockCount * sizeof uint16_t;

			// Just to be safe, align block
			size = Align(size);

			// PackedBlock blocksOfMaximumSize[blockCount];
			size += blockCount * EntryBlockSize;

			return size;
		}

	protected:
		void EnsureInitialized() override {
			if (!m_mergedHeader.empty())
				return;

			const auto lock = std::lock_guard(m_mtx);
			if (!m_mergedHeader.empty())
				return;

			std::vector<uint16_t> subBlockSizes;
			std::vector<uint8_t> textureHeaderAndMipmapOffsets;

			auto entryHeader = PackedFileHeader{
				.HeaderSize = sizeof PackedFileHeader,
				.Type = PackedFileType::Texture,
				.DecompressedSize = static_cast<uint32_t>(m_stream->StreamSize()),
			};

			textureHeaderAndMipmapOffsets.resize(sizeof TextureHeader);
			ReadStream(*m_stream, 0, std::span(textureHeaderAndMipmapOffsets));

			const auto mipmapCount = *reinterpret_cast<const TextureHeader*>(&textureHeaderAndMipmapOffsets[0])->MipmapCount;
			textureHeaderAndMipmapOffsets.resize(sizeof TextureHeader + mipmapCount * sizeof uint32_t);
			ReadStream(*m_stream, sizeof TextureHeader, Internal::span_cast<uint32_t>(textureHeaderAndMipmapOffsets, sizeof TextureHeader, mipmapCount));

			const auto firstBlockOffset = *reinterpret_cast<const uint32_t*>(&textureHeaderAndMipmapOffsets[sizeof TextureHeader]);
			textureHeaderAndMipmapOffsets.resize(firstBlockOffset);
			const auto mipmapOffsets = Internal::span_cast<uint32_t>(textureHeaderAndMipmapOffsets, sizeof TextureHeader, mipmapCount);
			ReadStream(*m_stream, sizeof TextureHeader + mipmapOffsets.size_bytes(), std::span(textureHeaderAndMipmapOffsets).subspan(sizeof TextureHeader + mipmapOffsets.size_bytes()));
			const auto& texHeader = *reinterpret_cast<const TextureHeader*>(&textureHeaderAndMipmapOffsets[0]);

			m_mipmapSizes.resize(mipmapOffsets.size());
			for (size_t i = 0; i < mipmapOffsets.size(); ++i)
				m_mipmapSizes[i] = static_cast<uint32_t>(TextureRawDataLength(texHeader, i));

			// Actual data exists but the mipmap offset array after texture header does not bother to refer
			// to the ones after the first set of mipmaps?
			// For example: if there are mipmaps of 4x4, 2x2, 1x1, 4x4, 2x2, 1x2, 4x4, 2x2, and 1x1,
			// then it will record mipmap offsets only up to the first occurrence of 1x1.
			const auto repeatCount = mipmapOffsets.size() < 2 ? 1 : (size_t{} + mipmapOffsets[1] - mipmapOffsets[0]) / TextureRawDataLength(texHeader, 0);
			m_mipmapOffsetsWithRepeats = { mipmapOffsets.begin(), mipmapOffsets.end() };
			for (auto forceQuit = false; !forceQuit && (m_mipmapOffsetsWithRepeats.empty() || m_mipmapOffsetsWithRepeats.back() + m_mipmapSizes.back() * repeatCount < entryHeader.DecompressedSize);) {
				for (uint16_t i = 0; i < mipmapCount; ++i) {

					// <caused by TexTools export>
					const auto size = static_cast<uint32_t>(TextureRawDataLength(texHeader, i));
					if (m_mipmapOffsetsWithRepeats.back() + m_mipmapSizes.back() + size > entryHeader.DecompressedSize) {
						forceQuit = true;
						break;
					}
					// </caused by TexTools export>

					m_mipmapOffsetsWithRepeats.push_back(m_mipmapOffsetsWithRepeats.back() + m_mipmapSizes.back());
					m_mipmapSizes.push_back(static_cast<uint32_t>(TextureRawDataLength(texHeader, i)));
				}
			}

			auto blockOffsetCounter = firstBlockOffset;
			for (size_t i = 0; i < m_mipmapOffsetsWithRepeats.size(); ++i) {
				const auto mipmapSize = m_mipmapSizes[i];
				for (uint32_t repeatI = 0; repeatI < repeatCount; repeatI++) {
					const auto blockAlignment = Align<uint32_t>(mipmapSize, EntryBlockDataSize);
					PackedLodBlockLocator loc{
						.CompressedOffset = blockOffsetCounter,
						.CompressedSize = 0,
						.DecompressedSize = mipmapSize,
						.FirstBlockIndex = m_blockLocators.empty() ? 0 : m_blockLocators.back().FirstBlockIndex + m_blockLocators.back().BlockCount,
						.BlockCount = blockAlignment.Count,
					};

					blockAlignment.IterateChunked([&](uint32_t, const uint32_t offset, const uint32_t length) {
						PackedBlockHeader header{
							.HeaderSize = sizeof PackedBlockHeader,
							.Version = 0,
							.CompressedSize = PackedBlockHeader::CompressedSizeNotCompressed,
							.DecompressedSize = length,
						};
						const auto alignmentInfo = Align(sizeof header + length);

						m_size += alignmentInfo.Alloc;
						subBlockSizes.push_back(static_cast<uint16_t>(alignmentInfo.Alloc));
						blockOffsetCounter += subBlockSizes.back();
						loc.CompressedSize += subBlockSizes.back();

					}, m_mipmapOffsetsWithRepeats[i] + m_mipmapSizes[i] * repeatI);

					m_blockLocators.emplace_back(loc);
				}
			}

			entryHeader.BlockCountOrVersion = static_cast<uint32_t>(m_blockLocators.size());
			entryHeader.HeaderSize = static_cast<uint32_t>(XivRes::Align(
				sizeof entryHeader +
				std::span(m_blockLocators).size_bytes() +
				std::span(subBlockSizes).size_bytes()));
			entryHeader.SetSpaceUnits(m_size);

			m_mergedHeader.reserve(entryHeader.HeaderSize + m_blockLocators.front().CompressedOffset);
			m_mergedHeader.insert(m_mergedHeader.end(),
				reinterpret_cast<char*>(&entryHeader),
				reinterpret_cast<char*>(&entryHeader + 1));
			m_mergedHeader.insert(m_mergedHeader.end(),
				reinterpret_cast<char*>(&m_blockLocators.front()),
				reinterpret_cast<char*>(&m_blockLocators.back() + 1));
			m_mergedHeader.insert(m_mergedHeader.end(),
				reinterpret_cast<char*>(&subBlockSizes.front()),
				reinterpret_cast<char*>(&subBlockSizes.back() + 1));
			m_mergedHeader.resize(entryHeader.HeaderSize);
			m_mergedHeader.insert(m_mergedHeader.end(),
				textureHeaderAndMipmapOffsets.begin(),
				textureHeaderAndMipmapOffsets.end());
			m_mergedHeader.resize(entryHeader.HeaderSize + m_blockLocators.front().CompressedOffset);

			m_size += m_mergedHeader.size();
		}

		std::streamsize TranslateRead(std::streamoff offset, void* buf, std::streamsize length) override {
			if (!length)
				return 0;

			const auto& packedFileHeader = *reinterpret_cast<const PackedFileHeader*>(&m_mergedHeader[0]);
			const auto& texHeader = *reinterpret_cast<const TextureHeader*>(&m_mergedHeader[packedFileHeader.HeaderSize]);

			auto relativeOffset = static_cast<uint64_t>(offset);
			auto out = std::span(static_cast<char*>(buf), static_cast<size_t>(length));

			// 1. Read headers and locators
			if (relativeOffset < m_mergedHeader.size()) {
				const auto src = std::span(m_mergedHeader)
					.subspan(static_cast<size_t>(relativeOffset));
				const auto available = (std::min)(out.size_bytes(), src.size_bytes());
				std::copy_n(src.begin(), available, out.begin());
				out = out.subspan(available);
				relativeOffset = 0;

				if (out.empty()) return length;
			} else
				relativeOffset -= m_mergedHeader.size();

			// 2. Read data blocks
			if (relativeOffset < m_size - m_mergedHeader.size()) {

				// 1. Find the first LOD block
				relativeOffset += m_blockLocators[0].CompressedOffset;
				auto it = std::ranges::lower_bound(m_blockLocators, PackedLodBlockLocator{ .CompressedOffset = static_cast<uint32_t>(relativeOffset) },
					[&](const auto& l, const auto& r) { return l.CompressedOffset < r.CompressedOffset; });
				if (it == m_blockLocators.end() || relativeOffset < it->CompressedOffset)
					--it;
				relativeOffset -= it->CompressedOffset;

				// 2. Iterate through LOD block headers
				for (; it != m_blockLocators.end(); ++it) {
					const auto blockIndex = it - m_blockLocators.begin();
					auto j = relativeOffset / EntryBlockSize;
					relativeOffset -= j * EntryBlockSize;

					// Iterate through packed blocks belonging to current LOD block
					for (; j < it->BlockCount; ++j) {
						const auto decompressedSize = j == it->BlockCount - 1 ? m_mipmapSizes[blockIndex] % EntryBlockDataSize : EntryBlockDataSize;
						const auto pad = Align(sizeof PackedBlockHeader + decompressedSize).Pad;

						// 1. Read packed block header
						if (relativeOffset < sizeof PackedBlockHeader) {
							const auto header = PackedBlockHeader{
								.HeaderSize = sizeof PackedBlockHeader,
								.Version = 0,
								.CompressedSize = PackedBlockHeader::CompressedSizeNotCompressed,
								.DecompressedSize = decompressedSize,
							};
							const auto src = Internal::span_cast<uint8_t>(1, &header).subspan(static_cast<size_t>(relativeOffset));
							const auto available = (std::min)(out.size_bytes(), src.size_bytes());
							std::copy_n(src.begin(), available, out.begin());
							out = out.subspan(available);
							relativeOffset = 0;

							if (out.empty()) return length;
						} else
							relativeOffset -= sizeof PackedBlockHeader;

						// 2. Read packed block data
						if (relativeOffset < decompressedSize) {
							const auto available = (std::min)(out.size_bytes(), static_cast<size_t>(decompressedSize - relativeOffset));
							ReadStream(*m_stream, m_mipmapOffsetsWithRepeats[blockIndex] + j * EntryBlockDataSize + relativeOffset, &out[0], available);
							out = out.subspan(available);
							relativeOffset = 0;

							if (out.empty()) return length;
						} else
							relativeOffset -= decompressedSize;

						// 3. Fill padding with zero
						if (relativeOffset < pad) {
							const auto available = (std::min)(out.size_bytes(), pad);
							std::fill_n(&out[0], available, 0);
							out = out.subspan(available);
							relativeOffset = 0;

							if (out.empty()) return length;
						} else {
							relativeOffset -= pad;
						}
					}
				}
			}

			// 3. Fill remainder with zero
			if (const auto endPadSize = static_cast<uint64_t>(StreamSize() - m_size); relativeOffset < endPadSize) {
				const auto available = (std::min)(out.size_bytes(), static_cast<size_t>(endPadSize - relativeOffset));
				std::fill_n(out.begin(), available, 0);
				out = out.subspan(static_cast<size_t>(available));
			}

			return length - out.size_bytes();
		}
	};

	class TextureCompressingPacker : public CompressingFilePacker<PackedFileType::Texture> {
	public:
		std::unique_ptr<IStream> Pack(const IStream& stream, int compressionLevel) const override {
			std::vector<uint8_t> textureHeaderAndMipmapOffsets;

			const auto rawStreamSize = static_cast<uint32_t>(stream.StreamSize());

			textureHeaderAndMipmapOffsets.resize(sizeof TextureHeader);
			ReadStream(stream, 0, std::span(textureHeaderAndMipmapOffsets));

			const auto mipmapCount = *reinterpret_cast<const TextureHeader*>(&textureHeaderAndMipmapOffsets[0])->MipmapCount;
			textureHeaderAndMipmapOffsets.resize(sizeof TextureHeader + mipmapCount * sizeof uint32_t);
			ReadStream(stream, sizeof TextureHeader, Internal::span_cast<uint32_t>(textureHeaderAndMipmapOffsets, sizeof TextureHeader, mipmapCount));

			const auto firstBlockOffset = *reinterpret_cast<const uint32_t*>(&textureHeaderAndMipmapOffsets[sizeof TextureHeader]);
			textureHeaderAndMipmapOffsets.resize(firstBlockOffset);
			const auto mipmapOffsets = Internal::span_cast<uint32_t>(textureHeaderAndMipmapOffsets, sizeof TextureHeader, mipmapCount);
			ReadStream(stream, sizeof TextureHeader + mipmapOffsets.size_bytes(), std::span(textureHeaderAndMipmapOffsets).subspan(sizeof TextureHeader + mipmapOffsets.size_bytes()));
			const auto& texHeader = *reinterpret_cast<const TextureHeader*>(&textureHeaderAndMipmapOffsets[0]);

			if (IsCancelled())
				return nullptr;

			std::vector<uint32_t> mipmapSizes(mipmapOffsets.size());
			for (size_t i = 0; i < mipmapOffsets.size(); ++i)
				mipmapSizes[i] = static_cast<uint32_t>(TextureRawDataLength(texHeader, i));

			// Actual data exists but the mipmap offset array after texture header does not bother to refer
			// to the ones after the first set of mipmaps?
			// For example: if there are mipmaps of 4x4, 2x2, 1x1, 4x4, 2x2, 1x2, 4x4, 2x2, and 1x1,
			// then it will record mipmap offsets only up to the first occurrence of 1x1.
			const auto repeatCount = mipmapOffsets.size() < 2 ? 1 : (size_t{} + mipmapOffsets[1] - mipmapOffsets[0]) / TextureRawDataLength(texHeader, 0);
			std::vector<uint32_t> mipmapOffsetsWithRepeats(mipmapOffsets.begin(), mipmapOffsets.end());
			for (auto forceQuit = false; !forceQuit && (mipmapOffsetsWithRepeats.empty() || mipmapOffsetsWithRepeats.back() + mipmapSizes.back() * repeatCount < rawStreamSize);) {
				for (uint16_t i = 0; i < mipmapCount; ++i) {

					// <caused by TexTools export>
					const auto size = static_cast<uint32_t>(TextureRawDataLength(texHeader, i));
					if (mipmapOffsetsWithRepeats.back() + mipmapSizes.back() + size > rawStreamSize) {
						forceQuit = true;
						break;
					}
					// </caused by TexTools export>

					mipmapOffsetsWithRepeats.push_back(mipmapOffsetsWithRepeats.back() + mipmapSizes.back());
					mipmapSizes.push_back(static_cast<uint32_t>(TextureRawDataLength(texHeader, i)));
				}
			}

			if (IsCancelled())
				return nullptr;

			std::vector<uint32_t> maxMipmapSizes;
			{
				maxMipmapSizes.reserve(mipmapOffsetsWithRepeats.size());
				std::vector<uint8_t> readBuffer;

				for (size_t i = 0; i < mipmapOffsetsWithRepeats.size(); ++i) {
					if (IsCancelled())
						return nullptr;

					uint32_t maxMipmapSize = 0;

					const auto minSize = (std::max)(4U, static_cast<uint32_t>(TextureRawDataLength(texHeader.Type, 1, 1, texHeader.Depth, i)));
					if (mipmapSizes[i] > minSize) {
						for (size_t repeatI = 0; repeatI < repeatCount; repeatI++) {
							if (IsCancelled())
								return nullptr;

							size_t offset = mipmapOffsetsWithRepeats[i] + mipmapSizes[i] * repeatI;
							auto mipmapSize = mipmapSizes[i];
							readBuffer.resize(mipmapSize);

							if (const auto read = static_cast<size_t>(stream.ReadStreamPartial(offset, &readBuffer[0], mipmapSize)); read != mipmapSize) {
								// <caused by TexTools export>
								std::fill_n(&readBuffer[read], mipmapSize - read, 0);
								// </caused by TexTools export>
							}

							for (auto nextSize = mipmapSize;; mipmapSize = nextSize) {
								if (IsCancelled())
									return nullptr;

								nextSize /= 2;
								if (nextSize < minSize)
									break;

								auto anyNonZero = false;
								for (const auto& v : std::span(readBuffer).subspan(nextSize, mipmapSize - nextSize))
									if ((anyNonZero = anyNonZero || v))
										break;
								if (anyNonZero)
									break;
							}
							maxMipmapSize = (std::max)(maxMipmapSize, mipmapSize);
						}
					} else {
						maxMipmapSize = mipmapSizes[i];
					}

					maxMipmapSize = mipmapSizes[i];  // TODO
					maxMipmapSizes.emplace_back(maxMipmapSize);
				}
			}

			std::vector<std::vector<std::vector<std::pair<bool, std::vector<uint8_t>>>>> blockDataList;
			size_t blockLocatorCount = 0, subBlockCount = 0;
			{
				blockDataList.resize(mipmapOffsetsWithRepeats.size());

				Internal::ThreadPool<> threadPool;
				std::vector<std::optional<Internal::ZlibReusableDeflater>> deflaters(threadPool.GetThreadCount());
				std::vector<std::vector<uint8_t>> readBuffers(threadPool.GetThreadCount());

				try {
					for (size_t i = 0; i < mipmapOffsetsWithRepeats.size(); ++i) {
						if (IsCancelled())
							return nullptr;

						blockDataList[i].resize(repeatCount);
						const auto mipmapSize = maxMipmapSizes[i];

						for (uint32_t repeatI = 0; repeatI < repeatCount; repeatI++) {
							if (IsCancelled())
								return nullptr;

							const auto blockAlignment = Align<uint32_t>(mipmapSize, EntryBlockDataSize);
							auto& blockDataVector = blockDataList[i][repeatI];
							blockDataVector.resize(blockAlignment.Count);
							subBlockCount += blockAlignment.Count;

							blockAlignment.IterateChunkedBreakable([&](const uint32_t index, const uint32_t offset, const uint32_t length) {
								if (IsCancelled())
									return false;

								threadPool.Submit([this, compressionLevel, index, offset, length, &stream, &readBuffers, &deflaters, &blockDataVector](size_t threadIndex) {
									if (IsCancelled())
										return;

									auto& readBuffer = readBuffers[threadIndex];
									auto& deflater = deflaters[threadIndex];
									if (compressionLevel && !deflater)
										deflater.emplace(compressionLevel, Z_DEFLATED, -15);

									readBuffer.clear();
									readBuffer.resize(length);
									if (const auto read = static_cast<size_t>(stream.ReadStreamPartial(offset, &readBuffer[0], length)); read != length) {
										// <caused by TexTools export>
										std::fill_n(&readBuffer[read], length - read, 0);
										// </caused by TexTools export>
									}

									if ((blockDataVector[index].first = deflater && deflater->Deflate(std::span(readBuffer)).size() < readBuffer.size()))
										blockDataVector[index].second = deflater->TakeResult();
									else
										blockDataVector[index].second = std::move(readBuffer);
								});

								return true;
							}, mipmapOffsetsWithRepeats[i] + mipmapSizes[i] * repeatI);

							blockLocatorCount++;
						}
					}

					threadPool.SubmitDoneAndWait();
				} catch (...) {
					// pass
				}

				threadPool.SubmitDoneAndWait();
			}

			if (IsCancelled())
				return nullptr;

			const auto entryHeaderLength = static_cast<uint16_t>(Align(0
				+ sizeof PackedFileHeader
				+ blockLocatorCount * sizeof PackedLodBlockLocator
				+ subBlockCount * sizeof uint16_t
			));
			size_t entryBodyLength = textureHeaderAndMipmapOffsets.size();
			for (const auto& repeatedItem : blockDataList) {
				for (const auto& mipmapItem : repeatedItem) {
					for (const auto& blockItem : mipmapItem) {
						entryBodyLength += Align(sizeof PackedBlockHeader + blockItem.second.size());
					}
				}
			}
			entryBodyLength = Align(entryBodyLength);

			std::vector<uint8_t> result(entryHeaderLength + entryBodyLength);

			auto& entryHeader = *reinterpret_cast<PackedFileHeader*>(&result[0]);
			entryHeader.Type = PackedFileType::Texture;
			entryHeader.DecompressedSize = rawStreamSize;
			entryHeader.BlockCountOrVersion = static_cast<uint32_t>(blockLocatorCount);
			entryHeader.HeaderSize = entryHeaderLength;
			entryHeader.SetSpaceUnits(entryBodyLength);

			const auto blockLocators = Internal::span_cast<PackedLodBlockLocator>(result, sizeof entryHeader, blockLocatorCount);
			const auto subBlockSizes = Internal::span_cast<uint16_t>(result, sizeof entryHeader + blockLocators.size_bytes(), subBlockCount);
			auto resultDataPtr = result.begin() + entryHeaderLength;
			resultDataPtr = std::copy(textureHeaderAndMipmapOffsets.begin(), textureHeaderAndMipmapOffsets.end(), resultDataPtr);

			auto blockOffsetCounter = static_cast<uint32_t>(std::span(textureHeaderAndMipmapOffsets).size_bytes());
			for (size_t i = 0, subBlockCounter = 0, blockLocatorIndexCounter = 0; i < mipmapOffsetsWithRepeats.size(); ++i) {
				if (IsCancelled())
					return nullptr;

				const auto maxMipmapSize = maxMipmapSizes[i];

				for (uint32_t repeatI = 0; repeatI < repeatCount; repeatI++) {
					if (IsCancelled())
						return nullptr;

					const auto blockAlignment = Align<uint32_t>(maxMipmapSize, EntryBlockDataSize);

					auto& loc = blockLocators[blockLocatorIndexCounter++];
					loc.CompressedOffset = blockOffsetCounter;
					loc.CompressedSize = 0;
					loc.DecompressedSize = maxMipmapSize;
					loc.FirstBlockIndex = blockLocators.empty() ? 0 : blockLocators.back().FirstBlockIndex + blockLocators.back().BlockCount;
					loc.BlockCount = blockAlignment.Count;

					blockAlignment.IterateChunkedBreakable([&](const uint32_t index, const uint32_t offset, const uint32_t length) {
						if (IsCancelled())
							return false;

						auto& [useCompressed, targetBuf] = blockDataList[i][repeatI][index];

						auto& header = *reinterpret_cast<PackedBlockHeader*>(&*resultDataPtr);
						header.HeaderSize = sizeof PackedBlockHeader;
						header.Version = 0;
						header.CompressedSize = useCompressed ? static_cast<uint32_t>(targetBuf.size()) : PackedBlockHeader::CompressedSizeNotCompressed;
						header.DecompressedSize = length;

						std::copy(targetBuf.begin(), targetBuf.end(), resultDataPtr + sizeof header);

						const auto& subBlockSize = subBlockSizes[subBlockCounter++] = static_cast<uint16_t>(Align(sizeof header + targetBuf.size()));
						blockOffsetCounter += subBlockSize;
						loc.CompressedSize += subBlockSize;
						resultDataPtr += subBlockSize;

						std::vector<uint8_t>().swap(targetBuf);

						return true;
					}, mipmapOffsetsWithRepeats[i] + mipmapSizes[i] * repeatI);
				}
			}

			return std::make_unique<MemoryStream>(std::move(result));
		}
	};
}

#endif
