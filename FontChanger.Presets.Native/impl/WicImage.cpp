#include "pch.h"
#include "FontChanger.Presets/WicImage.h"

#include <wincodec.h>

#pragma comment(lib, "windowscodecs.lib")

_COM_SMARTPTR_TYPEDEF(IWICImagingFactory, __uuidof(IWICImagingFactory));
_COM_SMARTPTR_TYPEDEF(IWICStream, __uuidof(IWICStream));
_COM_SMARTPTR_TYPEDEF(IWICBitmapEncoder, __uuidof(IWICBitmapEncoder));
_COM_SMARTPTR_TYPEDEF(IWICBitmapFrameEncode, __uuidof(IWICBitmapFrameEncode));
_COM_SMARTPTR_TYPEDEF(IWICBitmapDecoder, __uuidof(IWICBitmapDecoder));
_COM_SMARTPTR_TYPEDEF(IWICBitmapFrameDecode, __uuidof(IWICBitmapFrameDecode));
_COM_SMARTPTR_TYPEDEF(IWICFormatConverter, __uuidof(IWICFormatConverter));

namespace {
	void EncodePngInto(IWICImagingFactory* factory, IStream* stream, int width, int height, std::span<const xivres::util::b8g8r8a8> pixels) {
		if (width <= 0 || height <= 0 || pixels.size() < static_cast<size_t>(width) * height)
			throw std::invalid_argument("Invalid image size");

		IWICBitmapEncoderPtr encoder;
		SuccessOrThrow(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder));
		SuccessOrThrow(encoder->Initialize(stream, WICBitmapEncoderNoCache));

		IWICBitmapFrameEncodePtr frame;
		SuccessOrThrow(encoder->CreateNewFrame(&frame, nullptr));
		SuccessOrThrow(frame->Initialize(nullptr));
		SuccessOrThrow(frame->SetSize(static_cast<UINT>(width), static_cast<UINT>(height)));

		auto format = GUID_WICPixelFormat32bppBGRA;
		SuccessOrThrow(frame->SetPixelFormat(&format));
		if (format != GUID_WICPixelFormat32bppBGRA)
			throw std::runtime_error("The PNG encoder does not take 32-bit BGRA pixels.");

		const auto stride = static_cast<UINT>(width * sizeof(xivres::util::b8g8r8a8));
		SuccessOrThrow(frame->WritePixels(
			static_cast<UINT>(height),
			stride,
			stride * static_cast<UINT>(height),
			const_cast<BYTE*>(reinterpret_cast<const BYTE*>(pixels.data()))));
		SuccessOrThrow(frame->Commit());
		SuccessOrThrow(encoder->Commit());
	}

	IWICImagingFactoryPtr CreateFactory() {
		IWICImagingFactoryPtr factory;
		SuccessOrThrow(factory.CreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER));
		return factory;
	}
}

std::vector<uint8_t> FontChanger::WicImage::EncodePng(int width, int height, std::span<const xivres::util::b8g8r8a8> pixels) {
	const auto factory = CreateFactory();

	IStreamPtr stream;
	SuccessOrThrow(CreateStreamOnHGlobal(nullptr, TRUE, &stream));
	EncodePngInto(factory, stream, width, height, pixels);

	STATSTG stat;
	SuccessOrThrow(stream->Stat(&stat, STATFLAG_NONAME));
	std::vector<uint8_t> res(static_cast<size_t>(stat.cbSize.QuadPart));
	SuccessOrThrow(stream->Seek({}, STREAM_SEEK_SET, nullptr));
	ULONG read = 0;
	SuccessOrThrow(stream->Read(res.data(), static_cast<ULONG>(res.size()), &read));
	res.resize(read);
	return res;
}

void FontChanger::WicImage::SavePng(int width, int height, std::span<const xivres::util::b8g8r8a8> pixels, const std::filesystem::path& path) {
	const auto factory = CreateFactory();

	IWICStreamPtr stream;
	SuccessOrThrow(factory->CreateStream(&stream));
	SuccessOrThrow(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
	EncodePngInto(factory, stream, width, height, pixels);
}

void FontChanger::WicImage::SavePng(const xivres::texture::memory_mipmap_stream& mipmap, const std::filesystem::path& path) {
	SavePng(mipmap.Width, mipmap.Height, mipmap.as_span<xivres::util::b8g8r8a8>(), path);
}

std::vector<uint32_t> FontChanger::WicImage::DecodeBgra(std::span<const uint8_t> data, int& width, int& height) {
	// Fonts may be made on threads that have not started COM.
	const auto hrInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	const auto uninit = xivres::util::on_dtor([hrInit] {
		if (SUCCEEDED(hrInit))
			CoUninitialize();
	});

	std::vector<uint32_t> pixels;
	{
		const auto factory = CreateFactory();

		IWICStreamPtr stream;
		SuccessOrThrow(factory->CreateStream(&stream));
		SuccessOrThrow(stream->InitializeFromMemory(const_cast<BYTE*>(data.data()), static_cast<DWORD>(data.size())));

		IWICBitmapDecoderPtr decoder;
		SuccessOrThrow(factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder));

		IWICBitmapFrameDecodePtr frame;
		SuccessOrThrow(decoder->GetFrame(0, &frame));

		IWICFormatConverterPtr converter;
		SuccessOrThrow(factory->CreateFormatConverter(&converter));
		SuccessOrThrow(converter->Initialize(frame, GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));

		UINT w, h;
		SuccessOrThrow(converter->GetSize(&w, &h));
		if (!w || !h || w > 16384 || h > 16384)
			throw std::runtime_error("Invalid image size");
		pixels.resize(static_cast<size_t>(w) * h);
		SuccessOrThrow(converter->CopyPixels(nullptr, w * 4, static_cast<UINT>(pixels.size() * 4), reinterpret_cast<BYTE*>(pixels.data())));
		width = static_cast<int>(w);
		height = static_cast<int>(h);
	}
	return pixels;
}
