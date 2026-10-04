#pragma once

namespace App {
	// Renders a text with a font of a configuration and saves it as a PNG file, when the arguments ask to:
	//
	//   XivRes.FontGenerator64.exe <config.json> --render-text <text> --output <image.png> [--font <name>] [--max-width <px>]
	//
	// In the text, \n starts a new line, and \\ is a backslash. The font is the first of the configuration if not given.
	// Returns the exit code of the process, or nothing if the arguments do not ask for rendering.
	std::optional<int> RunCommandLineRender(const std::vector<std::wstring>& args);
}
