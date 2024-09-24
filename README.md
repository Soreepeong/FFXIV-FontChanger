# Font Mod Generator for FFXIV
Create a font mod for your game, to change the font displayed and/or add support for scripts of languages that aren't natively supported by the game.

| Program | Example |
| -- | -- |
| ![Program UI](https://user-images.githubusercontent.com/3614868/172542113-9b4005f9-b748-4faf-8759-a39827517832.png) | ![Comic Sans example](https://github.com/user-attachments/assets/0e7e952d-0e43-4faf-b535-c5cd9d551b4f) |

## Affected fonts
* Standard text, including dialogues, chat logs, and more: edit AXIS.
* Damage numbers: edit Jupiter.
* Bar numbers: edit Meidinger.
* Window titles: edit TrumpGothic.

## Unaffected fonts
* Text displayed on entering a zone.
* Text displayed on forming a light/full party.
* Gold saucer airplane piloting score.
* Other various giant gold text.

## Usage
1. If you're using the localized game release (not in Japanese, English, German, or French) then pick `ChnAXIS` or `KrnAXIS` from `File > New`.
2. Choose a single font from the list on the left.
3. Add and edit font elements to the list on the right top.
4. Try editing anything you can and see what happens. Alternatively, use `File > Open` and open a preset file to see examples.
5. Once finished, use `Export > Export to .ttmp TexTools ModPack file (Compress while packing)`, and import the file using whatever modding tool of your choice.
6. If the game is running, `Hot Reload` can be used if you do not want to restart the game. Note that this feature is not reliable and may crash your game.
