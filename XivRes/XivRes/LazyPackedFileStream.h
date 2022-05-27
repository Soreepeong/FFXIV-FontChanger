#ifndef _XIVRES_LAZYPACKEDFILESTREAM_H_
#define _XIVRES_LAZYPACKEDFILESTREAM_H_

#include <functional>
#include <type_traits>

#include <zlib.h>

#include "PackedFileStream.h"
#include "Internal/ThreadPool.h"

namespace XivRes {
	class IPassthroughFilePacker {
	public:
		virtual ~IPassthroughFilePacker() = default;

		[[nodiscard]] virtual PackedFileType GetPackedFileType() = 0;

		[[nodiscard]] virtual std::streamsize StreamSize() = 0;

		[[nodiscard]] virtual std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) = 0;
	};

	template<PackedFileType TPackedFileType>
	class AbstractPassthroughFilePacker : public IPassthroughFilePacker {
	protected:
		const std::shared_ptr<const IStream> m_stream;
		size_t m_size = 0;

		virtual void EnsureInitialized() = 0;

		virtual std::streamsize TranslateRead(std::streamoff offset, void* buf, std::streamsize length) = 0;

	public:
		AbstractPassthroughFilePacker(std::shared_ptr<const IStream> stream)
			: m_stream(std::move(stream)) {}

		[[nodiscard]] PackedFileType GetPackedFileType() override final {
			return TPackedFileType;
		}

		std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) override final {
			EnsureInitialized();
			return TranslateRead(offset, buf, length);
		}
	};

	template<typename TPacker, typename = std::enable_if_t<std::is_base_of_v<IPassthroughFilePacker, TPacker>>>
	class PassthroughPackedFileStream : public PackedFileStream {
		const std::shared_ptr<const IStream> m_stream;
		mutable TPacker m_packer;

	public:
		PassthroughPackedFileStream(SqpackPathSpec spec, std::shared_ptr<const IStream> stream)
			: PackedFileStream(std::move(spec))
			, m_packer(std::move(stream)) {}

		[[nodiscard]] std::streamsize StreamSize() const final {
			return m_packer.StreamSize();
		}

		std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) const final {
			return m_packer.ReadStreamPartial(offset, buf, length);
		}

		PackedFileType GetPackedFileType() const final {
			return m_packer.GetPackedFileType();
		}
	};

	template<PackedFileType TPackedFileType>
	class CompressingFilePacker {
	public:
		static constexpr auto Type = TPackedFileType;

	private:
		bool m_bCancel = false;

	protected:
		bool IsCancelled() const {
			return m_bCancel;
		}

	public:
		void Cancel() {
			m_bCancel = true;
		}

		virtual std::unique_ptr<IStream> Pack(const IStream& rawStream, int compressionLevel) const = 0;
	};

	template<typename TPacker>
	class CompressingPackedFileStream : public PackedFileStream {
		constexpr static int CompressionLevel_AlreadyPacked = Z_BEST_COMPRESSION + 1;

		mutable std::mutex m_mtx;
		mutable std::shared_ptr<const IStream> m_stream;
		mutable int m_compressionLevel;

	public:
		CompressingPackedFileStream(SqpackPathSpec spec, std::shared_ptr<const IStream> stream, int compressionLevel = Z_BEST_COMPRESSION)
			: PackedFileStream(std::move(spec))
			, m_stream(std::move(stream))
			, m_compressionLevel(compressionLevel) {}

		[[nodiscard]] std::streamsize StreamSize() const final {
			EnsureInitialized();
			return m_stream->StreamSize();
		}

		std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) const final {
			EnsureInitialized();
			return m_stream->ReadStreamPartial(offset, buf, length);
		}

		PackedFileType GetPackedFileType() const final {
			return TPacker::Type;
		}

	private:
		void EnsureInitialized() const {
			if (m_compressionLevel == CompressionLevel_AlreadyPacked)
				return;

			const auto lock = std::lock_guard(m_mtx);
			if (m_compressionLevel == CompressionLevel_AlreadyPacked)
				return;

			auto newStream = TPacker().Pack(*m_stream, m_compressionLevel);
			if (!newStream)
				throw std::logic_error("TODO; cancellation currently unhandled");

			m_stream = std::move(newStream);
			m_compressionLevel = CompressionLevel_AlreadyPacked;
		}
	};

	class LazyPackedFileStream : public PackedFileStream {
		mutable std::mutex m_initializationMutex;
		mutable bool m_initialized = false;
		bool m_cancelled = false;

	protected:
		const std::filesystem::path m_path;
		const std::shared_ptr<const IStream> m_stream;
		const uint64_t m_originalSize;
		const int m_compressionLevel;

	public:
		LazyPackedFileStream(SqpackPathSpec spec, std::filesystem::path path, int compressionLevel = Z_BEST_COMPRESSION)
			: PackedFileStream(std::move(spec))
			, m_path(std::move(path))
			, m_stream(std::make_shared<FileStream>(m_path))
			, m_originalSize(m_stream->StreamSize())
			, m_compressionLevel(compressionLevel) {
		}

		LazyPackedFileStream(SqpackPathSpec spec, std::shared_ptr<const IStream> stream, int compressionLevel = Z_BEST_COMPRESSION)
			: PackedFileStream(std::move(spec))
			, m_path()
			, m_stream(std::move(stream))
			, m_originalSize(m_stream->StreamSize())
			, m_compressionLevel(compressionLevel) {
		}

		[[nodiscard]] std::streamsize StreamSize() const final {
			if (const auto estimate = MaxPossibleStreamSize();
				estimate != SqpackDataHeader::MaxFileSize_MaxValue)
				return estimate;

			ResolveConst();
			return StreamSize(*m_stream);
		}

		std::streamsize ReadStreamPartial(std::streamoff offset, void* buf, std::streamsize length) const final {
			ResolveConst();

			const auto size = StreamSize(*m_stream);
			const auto estimate = MaxPossibleStreamSize();
			if (estimate == size || offset + length <= size)
				return ReadStreamPartial(*m_stream, offset, buf, length);

			if (offset >= estimate)
				return 0;
			if (offset + length > estimate)
				length = estimate - offset;

			auto target = std::span(static_cast<char*>(buf), static_cast<size_t>(length));
			if (offset < size) {
				const auto read = static_cast<size_t>(ReadStreamPartial(*m_stream, offset, &target[0], (std::min<uint64_t>)(size - offset, target.size())));
				target = target.subspan(read);
			}

			const auto remaining = static_cast<size_t>((std::min<uint64_t>)(estimate - size, target.size()));
			std::ranges::fill(target.subspan(0, remaining), 0);
			return length - (target.size() - remaining);
		}

		void Resolve() {
			if (m_initialized)
				return;

			const auto lock = std::lock_guard(m_initializationMutex);
			if (m_initialized)
				return;

			Initialize(*m_stream);
			m_initialized = true;
		}

	protected:
		void ResolveConst() const {
			const_cast<LazyPackedFileStream*>(this)->Resolve();
		}

		virtual void Initialize(const IStream& stream) {
			// does nothing
		}

		[[nodiscard]] virtual std::streamsize MaxPossibleStreamSize() const {
			return SqpackDataHeader::MaxFileSize_MaxValue;
		}

		[[nodiscard]] virtual std::streamsize StreamSize(const IStream& stream) const = 0;

		virtual std::streamsize ReadStreamPartial(const IStream& stream, std::streamoff offset, void* buf, std::streamsize length) const = 0;
	};
}

#endif
