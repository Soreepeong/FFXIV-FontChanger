#ifndef MAINWINDOW_INTERNAL_H
#define MAINWINDOW_INTERNAL_H

enum : uint8_t {
	ListViewColsFamilyName,
	ListViewColsSubfamilyName,
	ListViewColsSize,
	ListViewColsLineHeight,
	ListViewColsAscent,
	ListViewColsHorizontalOffset,
	ListViewColsLetterSpacing,
	ListViewColsGamma,
	ListViewColsCodepoints,
	ListViewColsGlyphCount,
	ListViewColsMergeMode,
	ListViewColsRenderer,
	ListViewColsLookup,
};

static constexpr auto FaceListBoxWidth = 160;
static constexpr auto FaceElementListViewHeight = 160;
static constexpr auto PreviewEditHeight = 60;
static constexpr auto SplitterThickness = 4;
static constexpr auto MinFaceElementListViewHeight = 80;
static constexpr auto MinPreviewEditHeight = 60;
static constexpr auto MinPreviewHeight = 120;

static constexpr auto PreviewZoomOne = 100;
static constexpr auto PreviewZoomMin = 100;
static constexpr auto PreviewZoomScrollUnit = 100;
static constexpr auto PreviewZoomMax = 1600;

static constexpr GUID Guid_IFileDialog_Json{0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x67}};
static constexpr GUID Guid_IFileDialog_Export{0x5c2fc703, 0x7406, 0x4704, {0x92, 0x12, 0xae, 0x41, 0x1d, 0x4b, 0x74, 0x68}};

#endif
