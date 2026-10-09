#include "pch.h"
#include "FaceElementEditorDialog.Internal.h"
#include "FontChanger.Presets/DirectWriteUtil.h"

using namespace App::FaceElementEditorDialogInternal;

INT_PTR App::FaceElementEditorDialog::FontRendererCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (const auto v = GetComboboxSelData<Structs::RendererEnum>(m_controls->FontRendererCombo);
		v != m_element.Renderer) {
		m_element.Renderer = v;
		if (!m_element.Lookup.Synthesis)
			m_element.Lookup.Synthesis = Structs::SynthesisStruct{};
		SetControlsEnabledOrDisabled();
		RepopulateFontCombobox();
		OnBaseFontChanged();
		RefreshUnicodeBlockSearchResults();
	}
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontCombo_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	if (UsesSystemFont(m_element.Renderer)) {
		const auto familyIndex = ComboBox_GetCurSel(m_controls->FontCombo);
		if (familyIndex < 0 || familyIndex >= static_cast<int>(m_fontFamilies.size()))
			return 0;

		const auto name = FontChanger::DirectWriteUtil::GetEnglishFamilyName(m_fontFamilies[familyIndex]);
		if (name.empty())
			return -1;
		m_element.Lookup.Name = xivres::util::unicode::convert<std::string>(name);
	} else {
		std::wstring name(ComboBox_GetTextLength(m_controls->FontCombo) + 1, L'\0');
		name.resize(ComboBox_GetText(m_controls->FontCombo, name.data(), static_cast<int>(name.size())));
		m_element.Lookup.Name = xivres::util::unicode::convert<std::string>(name);
	}

	RepopulateFontSubComboBox(true);
	OnBaseFontChanged();
	RefreshUnicodeBlockSearchResults();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::NumberEdit_OnCommand(uint16_t id, uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	// The edits, the values that they set and those values when the dialog opened, and whether the values change the
	// font before it is wrapped.
	auto& e = m_element;
	const auto& o = m_elementOriginal;
	const std::array<std::tuple<int, HWND, float*, float, bool>, 7> fields{{
		{IDC_EDIT_FONT_SIZE, m_controls->FontSizeEdit, &e.Size, o.Size, true},
		{IDC_EDIT_EMPTY_ASCENT, m_controls->EmptyAscentEdit, &e.RendererSpecific.Empty.Ascent, o.RendererSpecific.Empty.Ascent, true},
		{IDC_EDIT_EMPTY_LINEHEIGHT, m_controls->EmptyLineHeightEdit, &e.RendererSpecific.Empty.LineHeight, o.RendererSpecific.Empty.LineHeight, true},
		{IDC_EDIT_ADJUSTMENT_BASELINESHIFT, m_controls->AdjustmentBaselineShiftEdit, &e.WrapModifiers.BaselineShift, o.WrapModifiers.BaselineShift, false},
		{IDC_EDIT_ADJUSTMENT_LETTERSPACING, m_controls->AdjustmentLetterSpacingEdit, &e.WrapModifiers.LetterSpacing, o.WrapModifiers.LetterSpacing, false},
		{IDC_EDIT_ADJUSTMENT_HORIZONTALOFFSET, m_controls->AdjustmentHorizontalOffsetEdit, &e.WrapModifiers.HorizontalOffset, o.WrapModifiers.HorizontalOffset, false},
		{IDC_EDIT_ADJUSTMENT_GAMMA, m_controls->AdjustmentGammaEdit, &e.Gamma, o.Gamma, true},
	}};
	const auto it = std::ranges::find(fields, static_cast<int>(id), [](const auto& f) { return std::get<0>(f); });
	if (it == fields.end())
		return 0;

	const auto& [_, hwnd, field, original, isBase] = *it;
	if (!TryGetOrEvaluateValueInto(hwnd, *field, original))
		return 0;

	if (isBase)
		OnBaseFontChanged();
	else
		OnWrappedFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::SettingCombo_OnCommand(uint16_t id, uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE)
		return 0;

	// Sets the field to the value of the selected item; returns whether it changed.
	const auto set = []<typename T>(HWND combo, T& field) {
		const auto value = GetComboboxSelData<T>(combo);
		if (value == field)
			return false;
		field = value;
		return true;
	};

	auto& lookup = m_element.Lookup;
	auto& directWrite = m_element.RendererSpecific.DirectWrite;
	bool changed;
	switch (id) {
		case IDC_COMBO_FONT_WEIGHT: changed = set(m_controls->FontWeightCombo, lookup.Weight); break;
		case IDC_COMBO_FONT_STYLE: changed = set(m_controls->FontStyleCombo, lookup.Style); break;
		case IDC_COMBO_FONT_STRETCH: changed = set(m_controls->FontStretchCombo, lookup.Stretch); break;
		case IDC_COMBO_FREETYPE_RENDERMODE: changed = set(m_controls->FreeTypeRenderModeCombo, m_element.RendererSpecific.FreeType.RenderMode); break;
		case IDC_COMBO_DIRECTWRITE_RENDERMODE: changed = set(m_controls->DirectWriteRenderModeCombo, directWrite.RenderMode); break;
		case IDC_COMBO_DIRECTWRITE_MEASUREMODE: changed = set(m_controls->DirectWriteMeasureModeCombo, directWrite.MeasureMode); break;
		case IDC_COMBO_DIRECTWRITE_GRIDFITMODE: changed = set(m_controls->DirectWriteGridFitModeCombo, directWrite.GridFitMode); break;
		default: return 0;
	}
	if (!changed)
		return 0;

	// The face that the weight, style, and stretch choose may have other axes.
	if (id == IDC_COMBO_FONT_WEIGHT || id == IDC_COMBO_FONT_STYLE || id == IDC_COMBO_FONT_STRETCH)
		RepopulateFontVariationsList();
	OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FontAllowSynthesisCheck_OnCommand(uint16_t notiCode) {
	if (notiCode != BN_CLICKED)
		return 0;

	const auto allow = Button_GetCheck(m_controls->FontAllowSynthesisCheck) == BST_CHECKED;
	if (m_element.Lookup.Synthesis && m_element.Lookup.Synthesis->Allow == allow)
		return 0;

	m_element.Lookup.Synthesis = Structs::SynthesisStruct{.Allow = allow};
	RepopulateFontSubComboBox();
	OnBaseFontChanged();
	return 0;
}

INT_PTR App::FaceElementEditorDialog::FreeTypeHinting_OnCommand(uint16_t notiCode) {
	if (notiCode != CBN_SELCHANGE && notiCode != BN_CLICKED)
		return 0;

	const auto newFlags = GetComboboxSelData<int>(m_controls->FreeTypeHintingCombo)
		| (Button_GetCheck(m_controls->FreeTypeEmbeddedBitmapsCheck) ? 0 : FT_LOAD_NO_BITMAP);

	if (newFlags != m_element.RendererSpecific.FreeType.LoadFlags) {
		m_element.RendererSpecific.FreeType.LoadFlags = newFlags;
		OnBaseFontChanged();
	}
	return 0;
}

App::Structs::TransformStruct App::FaceElementEditorDialog::GetEditableTransform() const {
	return m_element.Transform;
}

void App::FaceElementEditorDialog::RefreshTransformEdits() {
	const auto t = GetEditableTransform();
	SetWindowNumber(m_controls->TransformScaleXEdit, t.ScaleX * 100.f);
	SetWindowNumber(m_controls->TransformScaleYEdit, t.ScaleY * 100.f);
	SetWindowNumber(m_controls->TransformSkewEdit, t.SkewDegrees);
	SetWindowNumber(m_controls->TransformRotationEdit, t.RotationDegrees);
}

INT_PTR App::FaceElementEditorDialog::TransformEdit_OnCommand(int index, uint16_t notiCode) {
	if (notiCode != EN_CHANGE)
		return 0;

	const auto current = GetEditableTransform();
	auto edited = current;

	// Scales are shown in percent.
	const std::array<std::pair<HWND, float*>, 4> fields{{
		{m_controls->TransformScaleXEdit, &edited.ScaleX},
		{m_controls->TransformScaleYEdit, &edited.ScaleY},
		{m_controls->TransformSkewEdit, &edited.SkewDegrees},
		{m_controls->TransformRotationEdit, &edited.RotationDegrees},
	}};
	const auto& [hwnd, field] = fields[index];
	const auto factor = index < 2 ? 100.f : 1.f;
	auto shown = *field * factor;
	if (!TryGetOrEvaluateValueInto(hwnd, shown, shown))
		return 0;
	*field = shown / factor;

	// A matrix of an earlier version is replaced only when a component actually changes.
	if (edited.ScaleX == current.ScaleX && edited.ScaleY == current.ScaleY && edited.SkewDegrees == current.SkewDegrees && edited.RotationDegrees == current.RotationDegrees)  // NOLINT(clang-diagnostic-float-equal)
		return 0;

	m_element.Transform = edited;
	OnBaseFontChanged();
	return 0;
}

void App::FaceElementEditorDialog::RepopulateFontCombobox() {
	ComboBox_ResetContent(m_controls->FontCombo);
	switch (m_element.Renderer) {
		case Structs::RendererEnum::Empty:
			break;

		case Structs::RendererEnum::PrerenderedGameInstallation: {
			auto anySel = false;
			for (const auto& family : FontChanger::FixedSizeFont::get_font_families()) {
				const auto name = xivres::util::unicode::convert<std::wstring>(family);
				ComboBox_AddString(m_controls->FontCombo, name.c_str());

				const auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
				if (lstrcmpiW(curNameLower.c_str(), name.c_str()) != 0)
					continue;

				anySel = true;
				ComboBox_SetCurSel(m_controls->FontCombo, ComboBox_GetCount(m_controls->FontCombo) - 1);
			}
			if (!anySel)
				ComboBox_SetCurSel(m_controls->FontCombo, 0);
			break;
		}

		case Structs::RendererEnum::DirectWrite:
		case Structs::RendererEnum::FreeType:
		case Structs::RendererEnum::GlyphImages: {
			IDWriteFactory3Ptr factory;
			SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(&factory)));

			IDWriteFontCollectionPtr coll;
			SuccessOrThrow(factory->GetSystemFontCollection(&coll));

			m_fontFamilies.clear();
			std::vector<std::wstring> names;
			auto curNameLower = xivres::util::unicode::convert<std::wstring>(m_element.Lookup.Name);
			CharLowerBuffW(curNameLower.data(), static_cast<DWORD>(curNameLower.size()));
			for (uint32_t i = 0, i_ = coll->GetFontFamilyCount(); i < i_; i++) {
				IDWriteFontFamilyPtr family;
				IDWriteLocalizedStringsPtr strings;

				if (FAILED(coll->GetFontFamily(i, &family)))
					continue;

				if (FAILED(family->GetFamilyNames(&strings)))
					continue;

				// Families are shown by their names in the language of the user, and are also found by their English names.
				const auto res = FontChanger::DirectWriteUtil::GetLocalizedString(strings, {g_localeName.c_str(), L"en-us", L"en"});
				auto englishLower = FontChanger::DirectWriteUtil::GetLocalizedString(strings, {L"en-us", L"en"});
				CharLowerBuffW(englishLower.data(), static_cast<DWORD>(englishLower.size()));

				std::wstring resLower = res;
				CharLowerBuffW(resLower.data(), static_cast<DWORD>(resLower.size()));

				const auto insertAt = std::ranges::lower_bound(names, resLower) - names.begin();

				ComboBox_InsertString(m_controls->FontCombo, insertAt, res.c_str());
				m_fontFamilies.insert(m_fontFamilies.begin() + insertAt, std::move(family));

				if (curNameLower == resLower || curNameLower == englishLower)
					ComboBox_SetCurSel(m_controls->FontCombo, insertAt);

				names.insert(names.begin() + insertAt, std::move(resLower));
			}

			break;
		}
		default:
			break;
	}

	RepopulateFontSubComboBox();
}

void App::FaceElementEditorDialog::RepopulateFontSubComboBox(bool snapToReal) {
	ComboBox_ResetContent(m_controls->FontWeightCombo);
	ComboBox_ResetContent(m_controls->FontStyleCombo);
	ComboBox_ResetContent(m_controls->FontStretchCombo);
	ListView_DeleteAllItems(m_controls->FontFeaturesList);
	m_features.clear();

	switch (m_element.Renderer) {
		case Structs::RendererEnum::PrerenderedGameInstallation: {
			ComboBox_SetCurSel(
				m_controls->FontWeightCombo,
				ComboBox_SetItemData(
					m_controls->FontWeightCombo,
					ComboBox_AddString(
						m_controls->FontWeightCombo,
						std::wstring(GetStringResource(IDS_FONTWEIGHT_400)).c_str()),
					m_element.Lookup.Weight = DWRITE_FONT_WEIGHT_NORMAL));
			ComboBox_SetCurSel(
				m_controls->FontStyleCombo,
				ComboBox_SetItemData(
					m_controls->FontStyleCombo,
					ComboBox_AddString(
						m_controls->FontStyleCombo,
						std::wstring(GetStringResource(IDS_FONTSTYLE_NORMAL)).c_str()),
					m_element.Lookup.Style = DWRITE_FONT_STYLE_NORMAL));
			ComboBox_SetCurSel(
				m_controls->FontStretchCombo, ComboBox_SetItemData(
					m_controls->FontStretchCombo,
					ComboBox_AddString(
						m_controls->FontStretchCombo,
						std::wstring(GetStringResource(IDS_FONTSTRETCH_NORMAL)).c_str()),
					m_element.Lookup.Stretch = DWRITE_FONT_STRETCH_NORMAL));
			break;
		}

		case Structs::RendererEnum::DirectWrite:
		case Structs::RendererEnum::FreeType:
		case Structs::RendererEnum::GlyphImages: {
			IDWriteFactory3Ptr factory;
			SuccessOrThrow(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(&factory)));

			IDWriteTextAnalyzerPtr analyzer;
			SuccessOrThrow(factory->CreateTextAnalyzer(&analyzer));

			IDWriteTextAnalyzer2Ptr analyzer2;
			SuccessOrThrow(analyzer->QueryInterface(&analyzer2));
			
			const auto curSel = (std::max)(0, (std::min)(static_cast<int>(m_fontFamilies.size() - 1), ComboBox_GetCurSel(m_controls->FontCombo)));
			const auto& family = m_fontFamilies[curSel];
			// Real faces only: the simulated fonts that DirectWrite lists are offered as synthesized ones instead.
			std::set<DWRITE_FONT_WEIGHT> weights;
			std::set<DWRITE_FONT_STYLE> styles;
			std::set<DWRITE_FONT_STRETCH> stretches;
			std::set<DWRITE_FONT_FEATURE_TAG> featureTags;
			std::optional<std::pair<float, float>> weightAxis, widthAxis;
			auto hasItalicAxis = false;

			for (uint32_t i = 0, i_ = family->GetFontCount(); i < i_; i++) {
				IDWriteFontPtr font;
				if (FAILED(family->GetFont(i, &font)))
					continue;

				if (font->GetSimulations() != DWRITE_FONT_SIMULATIONS_NONE)
					continue;

				weights.insert(font->GetWeight());
				styles.insert(font->GetStyle());
				stretches.insert(font->GetStretch());
				if (IDWriteFontFacePtr face; SUCCEEDED(font->CreateFontFace(&face))) {
					DWRITE_FONT_FEATURE_TAG tags[256];
					UINT32 tagCount{};
					if (SUCCEEDED(analyzer2->GetTypographicFeatures(
						face,
						{0, DWRITE_SCRIPT_SHAPES_DEFAULT},
						nullptr,
						_countof(tags),
						&tagCount,
						tags))) {
						featureTags.insert(tags, tags + tagCount);
					}

				}

				const auto merge = [](std::optional<std::pair<float, float>>& range, const DWRITE_FONT_AXIS_RANGE& r) {
					range = range ? std::make_pair((std::min)(range->first, r.minValue), (std::max)(range->second, r.maxValue)) : std::make_pair(r.minValue, r.maxValue);
				};
				for (const auto& range : FontChanger::DirectWriteUtil::GetFontAxisRanges(font)) {
					if (range.axisTag == DWRITE_FONT_AXIS_TAG_WEIGHT)
						merge(weightAxis, range);
					else if (range.axisTag == DWRITE_FONT_AXIS_TAG_WIDTH)
						merge(widthAxis, range);
					else if ((range.axisTag == DWRITE_FONT_AXIS_TAG_ITALIC && range.maxValue >= 1.f) || (range.axisTag == DWRITE_FONT_AXIS_TAG_SLANT && range.minValue < 0.f))
						hasItalicAxis = true;
				}
			}

			constexpr DWRITE_FONT_WEIGHT StandardWeights[]{
				DWRITE_FONT_WEIGHT_THIN,
				DWRITE_FONT_WEIGHT_EXTRA_LIGHT,
				DWRITE_FONT_WEIGHT_LIGHT,
				DWRITE_FONT_WEIGHT_NORMAL,
				DWRITE_FONT_WEIGHT_MEDIUM,
				DWRITE_FONT_WEIGHT_SEMI_BOLD,
				DWRITE_FONT_WEIGHT_BOLD,
				DWRITE_FONT_WEIGHT_EXTRA_BOLD,
				DWRITE_FONT_WEIGHT_BLACK,
			};

			// Values that the axes of variable fonts reach are real too, as ElementFonts::ResolveSynthesis sets the axes.
			for (const auto w : StandardWeights) {
				if (weightAxis && weightAxis->first <= static_cast<float>(w) && static_cast<float>(w) <= weightAxis->second)
					weights.insert(w);
			}
			for (auto s = DWRITE_FONT_STRETCH_ULTRA_CONDENSED; s <= DWRITE_FONT_STRETCH_ULTRA_EXPANDED; s = static_cast<DWRITE_FONT_STRETCH>(s + 1)) {
				if (const auto percent = ElementFonts::GetStretchPercent(s); widthAxis && widthAxis->first <= percent && percent <= widthAxis->second)
					stretches.insert(s);
			}
			if (hasItalicAxis)
				styles.insert(DWRITE_FONT_STYLE_ITALIC);
			stretches.erase(DWRITE_FONT_STRETCH_UNDEFINED);

			// What can be synthesized, as ElementFonts::ResolveSynthesis does: any weight for FreeType, and only bold from
			// faces up to Medium for DirectWrite; italics as an oblique slant; and any width by scaling.
			std::set<DWRITE_FONT_WEIGHT> fauxWeights;
			std::set<DWRITE_FONT_STYLE> fauxStyles;
			std::set<DWRITE_FONT_STRETCH> fauxStretches;
			if (m_element.Lookup.Synthesis && m_element.Lookup.Synthesis->Allow && !weights.empty()) {
				if (m_element.Renderer == Structs::RendererEnum::FreeType) {
					for (const auto w : StandardWeights) {
						if (!weights.contains(w))
							fauxWeights.insert(w);
					}
				} else if (*weights.rbegin() <= DWRITE_FONT_WEIGHT_MEDIUM) {
					fauxWeights.insert(DWRITE_FONT_WEIGHT_BOLD);
				}

				if (!styles.contains(DWRITE_FONT_STYLE_ITALIC) && !styles.contains(DWRITE_FONT_STYLE_OBLIQUE))
					fauxStyles.insert(DWRITE_FONT_STYLE_ITALIC);

				for (auto s = DWRITE_FONT_STRETCH_ULTRA_CONDENSED; s <= DWRITE_FONT_STRETCH_ULTRA_EXPANDED; s = static_cast<DWRITE_FONT_STRETCH>(s + 1)) {
					if (!stretches.contains(s))
						fauxStretches.insert(s);
				}
			}

			// Keeps the requested value if it is listed; otherwise picks the closest real one.
			const auto pick = [snapToReal]<typename T>(const std::set<T>& real, const std::set<T>& faux, T requested) {
				if (!snapToReal && (real.contains(requested) || faux.contains(requested)))
					return requested;
				if (real.empty())
					return requested;
				auto closest = *real.begin();
				for (const auto v : real) {
					if (std::abs(static_cast<int>(requested) - static_cast<int>(v)) < std::abs(static_cast<int>(requested) - static_cast<int>(closest)))
						closest = v;
				}
				return closest;
			};
			m_element.Lookup.Weight = pick(weights, fauxWeights, m_element.Lookup.Weight);
			m_element.Lookup.Style = pick(styles, fauxStyles, m_element.Lookup.Style);
			m_element.Lookup.Stretch = pick(stretches, fauxStretches, m_element.Lookup.Stretch);

			const auto fill = []<typename T>(HWND combo, const std::set<T>& real, const std::set<T>& faux, T selected, const auto& getLabel) {
				std::map<T, bool> items;
				for (const auto v : real)
					items.emplace(v, false);
				for (const auto v : faux)
					items.emplace(v, true);

				for (const auto& [v, isFaux] : items) {
					auto text = getLabel(v);
					if (isFaux)
						text = std::vformat(GetStringResource(IDS_FONTFACE_FAUX_FORMAT), std::make_wformat_args(text));
					const auto index = ComboBox_AddString(combo, text.c_str());
					ComboBox_SetItemData(combo, index, static_cast<uint32_t>(v));
					if (v == selected)
						ComboBox_SetCurSel(combo, index);
				}
			};
			fill(m_controls->FontWeightCombo, weights, fauxWeights, m_element.Lookup.Weight, &GetFontWeightName);
			fill(m_controls->FontStyleCombo, styles, fauxStyles, m_element.Lookup.Style, &GetFontStyleName);
			fill(m_controls->FontStretchCombo, stretches, fauxStretches, m_element.Lookup.Stretch, &GetFontStretchName);

			std::map<DWRITE_FONT_FEATURE_TAG, uint32_t> alternateCounts;
			std::set<DWRITE_FONT_FEATURE_TAG> defaultOn;
			try {
				const auto face = CreateHarfBuzzFace(m_element.Lookup);
				alternateCounts = GetFeatureAlternateCounts(face.get());
				defaultOn = GetDefaultOnFeatures(face.get(), m_element.Lookup.Language);

				// Kerning of the legacy kern table is not a feature that DirectWrite lists, but can be turned off as one.
				if (defaultOn.contains(DWRITE_FONT_FEATURE_TAG_KERNING))
					featureTags.insert(DWRITE_FONT_FEATURE_TAG_KERNING);
			} catch (...) {
				// Leave the values of features without alternates or defaults if the font cannot be read.
			}

			// Sort alphabetically by tag.
			std::vector sortedTags(featureTags.begin(), featureTags.end());
			std::ranges::sort(sortedTags, {}, [](DWRITE_FONT_FEATURE_TAG tag) { return _byteswap_ulong(static_cast<uint32_t>(tag)); });

			for (const auto tag : sortedTags) {
				const auto alternates = alternateCounts.contains(tag) ? alternateCounts.at(tag) : 0u;
				m_features.emplace_back(FeatureRow{ .Tag = tag, .Alternates = alternates, .DefaultOn = defaultOn.contains(tag) });

				auto name = std::format(
					L"{}: {}",
					xivres::util::unicode::convert<std::wstring>(std::string_view(reinterpret_cast<const char*>(&tag), 4)),
					GetOpenTypeFeatureName(tag));
				const auto index = static_cast<int>(m_features.size() - 1);
				LVITEMW lvi{
					.mask = LVIF_TEXT,
					.iItem = index,
					.pszText = name.data(),
				};
				ListView_InsertItem(m_controls->FontFeaturesList, &lvi);
				UpdateFontFeaturesListItem(index);
			}
			break;
		}

		default:
			break;
	}

	SetWindowNumber(m_controls->FontSizeEdit, m_element.Size);
	RefreshFontFeatureValueCombo();
	RepopulateFontVariationsList();
}
