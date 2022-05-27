#ifndef _XIVRES_TEXTUREPACKEDFILESTREAMDECODER_H_
#define _XIVRES_TEXTUREPACKEDFILESTREAMDECODER_H_

#include <mutex>

#include "SqpackStreamDecoder.h"
#include "Texture.h"

namespace XivRes {
	class TexturePackedFileStreamDecoder : public BasePackedFileStreamDecoder {
		struct BlockInfo {
			uint32_t RequestOffset;
			uint32_t BlockOffset;
			uint32_t RemainingDecompressedSize;
			std::vector<uint16_t> RemainingBlockSizes;

			bool operator<(uint32_t r) const {
				return RequestOffset < r;
			}
		};

		std::mutex m_mtx;

		std::vector<uint8_t> m_head;
		std::vector<BlockInfo> m_blocks;

	public:
		TexturePackedFileStreamDecoder(const PackedFileHeader& header, std::shared_ptr<const PackedFileStream> stream)
			: BasePackedFileStreamDecoder(std::move(stream)) {
			uint64_t readOffset = sizeof PackedFileHeader;
			const auto locators = ReadStreamIntoVector<PackedLodBlockLocator>(*m_stream, readOffset, header.BlockCountOrVersion);
			readOffset += std::span(locators).size_bytes();

			m_head = ReadStreamIntoVector<uint8_t>(*m_stream, header.HeaderSize, locators[0].CompressedOffset);

			const auto& texHeader = *reinterpret_cast<const TextureHeader*>(&m_head[0]);
			const auto mipmapOffsets = Internal::span_cast<uint32_t>(m_head, sizeof texHeader, texHeader.MipmapCount);

			const auto repeatCount = mipmapOffsets.size() < 2 ? 1 : (mipmapOffsets[1] - mipmapOffsets[0]) / static_cast<uint32_t>(TextureRawDataLength(texHeader, 0));

			for (uint32_t i = 0; i < locators.size(); ++i) {
				const auto& locator = locators[i];
				const auto mipmapIndex = i / repeatCount;
				const auto mipmapPlaneIndex = i % repeatCount;
				const auto mipmapPlaneSize = static_cast<uint32_t>(TextureRawDataLength(texHeader, mipmapIndex));
				uint32_t baseRequestOffset = 0;
				if (mipmapIndex < mipmapOffsets.size())
					baseRequestOffset = mipmapOffsets[mipmapIndex] - mipmapOffsets[0] + mipmapPlaneSize * mipmapPlaneIndex;
				else if (!m_blocks.empty())
					baseRequestOffset = m_blocks.back().RequestOffset + m_blocks.back().RemainingDecompressedSize;
				else
					baseRequestOffset = 0;
				m_blocks.emplace_back(BlockInfo{
					.RequestOffset = baseRequestOffset,
					.BlockOffset = header.HeaderSize + locator.CompressedOffset,
					.RemainingDecompressedSize = locator.DecompressedSize,
					.RemainingBlockSizes = ReadStreamIntoVector<uint16_t>(*m_stream, readOffset, locator.BlockCount),
					});
				readOffset += std::span(m_blocks.back().RemainingBlockSizes).size_bytes();
				baseRequestOffset += mipmapPlaneSize;
			}
		}

		std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) override {
			if (!length)
				return 0;

			ReadStreamState info{
				.TargetBuffer = std::span(static_cast<uint8_t*>(buf), static_cast<size_t>(length)),
				.RelativeOffset = offset,
			};

			if (info.RelativeOffset < static_cast<std::streamoff>(m_head.size())) {
				const auto available = (std::min)(info.TargetBuffer.size_bytes(), static_cast<size_t>(m_head.size() - info.RelativeOffset));
				const auto src = std::span(m_head).subspan(static_cast<size_t>(info.RelativeOffset), available);
				std::copy_n(src.begin(), available, info.TargetBuffer.begin());
				info.TargetBuffer = info.TargetBuffer.subspan(available);
				info.RelativeOffset = 0;
			} else
				info.RelativeOffset -= m_head.size();

			if (info.TargetBuffer.empty() || m_blocks.empty())
				return length - info.TargetBuffer.size_bytes();

			const auto lock = std::lock_guard(m_mtx);

			const auto streamSize = m_blocks.back().RequestOffset + m_blocks.back().RemainingDecompressedSize;

			if (info.RelativeOffset >= streamSize)
				return 0;

			size_t i = std::lower_bound(m_blocks.begin(), m_blocks.end(), static_cast<uint32_t>(info.RelativeOffset)) - m_blocks.begin();
			if (i != 0 && (i == m_blocks.size() || info.RelativeOffset < m_blocks[i].RequestOffset))
				i--;

			for (; i < m_blocks.size() && !info.TargetBuffer.empty(); i++) {
				auto& block = m_blocks[i];
				info.ProgressRead(*m_stream, block.BlockOffset, block.RemainingBlockSizes.empty() ? 16384 : block.RemainingBlockSizes.front());
				info.ProgressDecode(block.RequestOffset);

				if (block.RemainingBlockSizes.empty())
					continue;

				const auto& blockHeader = info.AsHeader();
				auto newBlockInfo = BlockInfo{
					.RequestOffset = block.RequestOffset + blockHeader.DecompressedSize,
					.BlockOffset = block.BlockOffset + block.RemainingBlockSizes.front(),
					.RemainingDecompressedSize = block.RemainingDecompressedSize - blockHeader.DecompressedSize,
					.RemainingBlockSizes = std::move(block.RemainingBlockSizes),
				};

				if (i + 1 >= m_blocks.size() || (m_blocks[i + 1].RequestOffset != newBlockInfo.RequestOffset && m_blocks[i + 1].BlockOffset != newBlockInfo.BlockOffset)) {
					newBlockInfo.RemainingBlockSizes.erase(newBlockInfo.RemainingBlockSizes.begin());
					m_blocks.emplace(m_blocks.begin() + i + 1, std::move(newBlockInfo));
				}
			}

			return length - info.TargetBuffer.size_bytes();
		}
	};
}

#endif
