#include "pch.h"
#include "FaceElementEditorDialog.Internal.h"
#include "FontChanger.Presets/CodepointRanges.h"

namespace {
	// Counts the codepoints of the set in the range.
	size_t CountInRange(const std::set<char32_t>& codepoints, char32_t c1, char32_t c2) {
		return static_cast<size_t>(std::distance(codepoints.lower_bound(c1), codepoints.upper_bound(c2)));
	}
}

INT_PTR App::FaceElementEditorDialog::CustomRangeEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	std::wstring description;
	for (const auto& [c1, c2] : ParseCustomRangeString()) {
		if (!description.empty())
			description += L", ";
		if (c1 == c2) {
			description += std::format(
				L"U+{:04X} {}",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			);
		} else {
			description += std::format(
				L"U+{:04X}~{:04X} {}~{}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			);
		}
	}

	Edit_SetText(m_controls->CustomRangePreview, description.data());

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CustomRangeAdd_OnCommand(uint16_t notiCode) {
	auto changed = false;
	for (const auto& [c1, c2] : ParseCustomRangeString())
		changed |= AddNewCodepointRange(c1, c2);

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CustomRangeSubtract_OnCommand(uint16_t notiCode) {
	RemoveCodepointRanges(ParseCustomRangeString());
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsList_OnCommand(uint16_t notiCode) {
	if (notiCode == LBN_DBLCLK)
		return CodepointsDeleteButton_OnCommand(BN_CLICKED);

	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsClearButton_OnCommand(uint16_t notiCode) {
	ListBox_ResetContent(m_controls->CodepointsList);
	m_element.WrapModifiers.Codepoints.clear();
	OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsDeleteButton_OnCommand(uint16_t notiCode) {
	std::vector<int> selItems(ListBox_GetSelCount(m_controls->CodepointsList));
	if (selItems.empty())
		return 0;

	ListBox_GetSelItems(m_controls->CodepointsList, static_cast<int>(selItems.size()), selItems.data());
	std::ranges::sort(selItems, std::greater<>());
	for (const auto itemIndex : selItems) {
		m_element.WrapModifiers.Codepoints.erase(m_element.WrapModifiers.Codepoints.begin() + itemIndex);
		ListBox_DeleteString(m_controls->CodepointsList, itemIndex);
	}

	OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::CodepointsMergeModeCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = static_cast<FontChanger::FixedSizeFont::codepoint_merge_mode>(ComboBox_GetCurSel(m_controls->CodepointsMergeModeCombo));
		v != m_element.MergeMode) {
		m_element.MergeMode = v;
		OnWrappedFontChanged();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchNameEdit_OnCommand(uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput_OnCommand(uint16_t notiCode) {
	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchResultList_OnCommand(uint16_t notiCode) {
	if (notiCode == LBN_SELCHANGE || notiCode == LBN_SELCANCEL) {
		std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
		if (selItems.empty())
			return 0;

		ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), selItems.data());

		const auto& codepoints = m_element.GetBaseFont()->all_codepoints();
		std::wstring containingChars;

		containingChars.reserve(8192);

		for (const auto itemIndex : selItems) {
			const auto& block = *reinterpret_cast<xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));

			for (auto it = codepoints.lower_bound(block.First), it_ = codepoints.upper_bound(block.Last); it != it_; ++it) {
				xivres::util::unicode::represent_codepoint(containingChars, *it);
				if (containingChars.size() >= 8192)
					break;
			}

			if (containingChars.size() >= 8192)
				break;
		}

		Static_SetText(m_controls->UnicodeBlockSearchSelectedPreviewEdit, containingChars.c_str());
	} else if (notiCode == LBN_DBLCLK) {
		return UnicodeBlockSearchAdd_OnCommand(BN_CLICKED);
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchAddAll_OnCommand(uint16_t notiCode) {
	auto changed = false;
	for (int i = 0, i_ = ListBox_GetCount(m_controls->UnicodeBlockSearchResultList); i < i_; i++) {
		const auto& block = *reinterpret_cast<const xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, i));
		changed |= AddNewCodepointRange(block.First, block.Last);
	}

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchAdd_OnCommand(uint16_t notiCode) {
	std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
	if (selItems.empty())
		return 0;

	ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), selItems.data());

	auto changed = false;
	for (const auto itemIndex : selItems) {
		const auto& block = *reinterpret_cast<const xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));
		changed |= AddNewCodepointRange(block.First, block.Last);
	}

	if (changed)
		OnWrappedFontChanged();

	return 0;
}

INT_PTR App::FaceElementEditorDialog::UnicodeBlockSearchSubtract_OnCommand(uint16_t notiCode) {
	std::vector<int> selItems(ListBox_GetSelCount(m_controls->UnicodeBlockSearchResultList));
	if (selItems.empty())
		return 0;

	ListBox_GetSelItems(m_controls->UnicodeBlockSearchResultList, static_cast<int>(selItems.size()), selItems.data());
	
	std::vector<std::pair<char32_t, char32_t>> ranges;
	ranges.reserve(selItems.size());
	for (const auto itemIndex : selItems) {
		const auto& block = *reinterpret_cast<const xivres::util::unicode::blocks::block_definition*>(ListBox_GetItemData(m_controls->UnicodeBlockSearchResultList, itemIndex));
		ranges.emplace_back(block.First, block.Last);
	}

	RemoveCodepointRanges(ranges);

	return 0;
}

std::vector<std::pair<char32_t, char32_t>> App::FaceElementEditorDialog::ParseCustomRangeString() {
	return FontChanger::CodepointRanges::Parse(GetWindowString(m_controls->CustomRangeEdit));
}

bool App::FaceElementEditorDialog::AddNewCodepointRange(char32_t c1, char32_t c2) {
	const auto newItem = std::make_pair(c1, c2);
	const auto it = std::ranges::lower_bound(m_element.WrapModifiers.Codepoints, newItem);
	if (it != m_element.WrapModifiers.Codepoints.end() && *it == newItem)
		return false;

	const auto newIndex = static_cast<int>(it - m_element.WrapModifiers.Codepoints.begin());
	m_element.WrapModifiers.Codepoints.insert(it, newItem);
	AddCodepointRangeToListBox(newIndex, c1, c2);
	return true;
}

void App::FaceElementEditorDialog::AddCodepointRangeToListBox(int index, char32_t c1, char32_t c2) {
	const auto count = CountInRange(m_element.GetBaseFont()->all_codepoints(), c1, c2);

	const auto block = std::lower_bound(xivres::util::unicode::blocks::all_blocks().begin(), xivres::util::unicode::blocks::all_blocks().end(), c1, [](const auto& l, const auto& r) { return l.First < r; });
	if (block != xivres::util::unicode::blocks::all_blocks().end() && block->First == c1 && block->Last == c2) {
		if (c1 == c2) {
			ListBox_InsertString(m_controls->CodepointsList, index, std::format(
				L"U+{:04X} {} [{}]",
				static_cast<uint32_t>(c1),
				xivres::util::unicode::convert<std::wstring>(block->Name),
				xivres::util::unicode::represent_codepoint<std::wstring>(c1)
			).c_str());
		} else {
			ListBox_InsertString(m_controls->CodepointsList, index, std::format(
				L"U+{:04X}~{:04X} {} ({}) {} ~ {}",
				static_cast<uint32_t>(c1),
				static_cast<uint32_t>(c2),
				xivres::util::unicode::convert<std::wstring>(block->Name),
				count,
				xivres::util::unicode::represent_codepoint<std::wstring>(c1),
				xivres::util::unicode::represent_codepoint<std::wstring>(c2)
			).c_str());
		}
	} else if (c1 == c2) {
		ListBox_InsertString(m_controls->CodepointsList, index, std::format(
			L"U+{:04X} [{}]",
			static_cast<int>(c1),
			xivres::util::unicode::represent_codepoint<std::wstring>(c1)
		).c_str());
	} else {
		ListBox_InsertString(m_controls->CodepointsList, index, std::format(
			L"U+{:04X}~{:04X} ({}) {} ~ {}",
			static_cast<uint32_t>(c1),
			static_cast<uint32_t>(c2),
			count,
			xivres::util::unicode::represent_codepoint<std::wstring>(c1),
			xivres::util::unicode::represent_codepoint<std::wstring>(c2)
		).c_str());
	}
}

void App::FaceElementEditorDialog::RemoveCodepointRanges(const std::vector<std::pair<char32_t, char32_t>>& ranges) {
	std::vector<std::pair<char32_t, char32_t>> codepoints(m_element.WrapModifiers.Codepoints);
	std::ranges::sort(codepoints);

	auto changed = false;
	for (const auto& [c1, c2] : ranges) {
		for (size_t i = 0; i < codepoints.size(); i++) {
			const auto [a, b] = codepoints[i];

			// a...........b
			// ..[c1...c2]..
			// => [a, c1-1], [c2+1, b]
			if (a < c1 && c2 < b) {
				codepoints[i].second = c1 - 1;
				codepoints.insert(codepoints.begin() + (i + 1), std::make_pair(c2 + 1, b));
				changed = true;
				continue;
			}

			// ...a..b...
			// [c1....c2]
			// => (delete)
			if (c1 <= a && b <= c2) {
				codepoints.erase(codepoints.begin() + i);
				changed = true;
				continue;
			}

			// .....a...b
			// [c1...c2]
			// => [c2+1, b]
			if (c1 <= a && a < c2 && c2 < b) {
				codepoints[i].first = c2 + 1;
				changed = true;
				continue;
			}

			// a...b
			// ..[c1...c2]
			// => [a, c1-1]
			if (a < c1 && c1 < b && b <= c2) {
				codepoints[i].second = c1 - 1;
				changed = true;
				continue;
			}
		}
	}

	if (changed) {
		m_element.WrapModifiers.Codepoints = std::move(codepoints);
		OnWrappedFontChanged();
		ListBox_ResetContent(m_controls->CodepointsList);
		for (int i = 0, i_ = static_cast<int>(m_element.WrapModifiers.Codepoints.size()); i < i_; i++)
			AddCodepointRangeToListBox(i, m_element.WrapModifiers.Codepoints[i].first, m_element.WrapModifiers.Codepoints[i].second);
	}

}

void App::FaceElementEditorDialog::RefreshUnicodeBlockSearchResults() {
	const auto input = xivres::util::unicode::convert<std::string>(GetWindowString(m_controls->UnicodeBlockSearchNameEdit));
	const auto input32 = xivres::util::unicode::convert<std::u32string>(input);
	ListBox_ResetContent(m_controls->UnicodeBlockSearchResultList);

	const auto searchByChar = Button_GetCheck(m_controls->UnicodeBlockSearchShowBlocksWithAnyOfCharactersInput);

	const auto& codepoints = m_element.GetBaseFont()->all_codepoints();
	for (const auto& block : xivres::util::unicode::blocks::all_blocks()) {
		const auto nameView = std::string_view(block.Name);
		const auto it = std::search(nameView.begin(), nameView.end(), input.begin(), input.end(), [](char ch1, char ch2) {
			return std::toupper(ch1) == std::toupper(ch2);
		});
		if (it == nameView.end()) {
			if (searchByChar) {
				auto contains = false;
				for (const auto& c : input32) {
					if (block.First <= c && block.Last >= c) {
						contains = true;
						break;
					}
				}
				if (!contains)
					continue;
			} else
				continue;
		}

		const auto count = CountInRange(codepoints, block.First, block.Last);
		if (!count)
			continue;

		ListBox_AddString(m_controls->UnicodeBlockSearchResultList, std::format(
			L"U+{:04X}~{:04X} {} ({}) {} ~ {}",
			static_cast<uint32_t>(block.First),
			static_cast<uint32_t>(block.Last),
			xivres::util::unicode::convert<std::wstring>(nameView),
			count,
			xivres::util::unicode::represent_codepoint<std::wstring>(block.First),
			xivres::util::unicode::represent_codepoint<std::wstring>(block.Last)
		).c_str());
		ListBox_SetItemData(m_controls->UnicodeBlockSearchResultList, ListBox_GetCount(m_controls->UnicodeBlockSearchResultList) - 1, &block);
	}
}
