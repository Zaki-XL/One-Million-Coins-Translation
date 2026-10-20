English | [日本語 (Japanese)](README.md)

# One Million Coins - Multilingual Translation Mod (Doorstop-based)

A non-invasive, Doorstop-based multilingual translation system for the Steam game "One Million Coins".

## Features
- **Zero Game File Modification**: Does not modify or overwrite any game executable, DLL, or assets.
- **Early Injection**: Uses the Windows DLL proxy mechanism (Doorstop `winhttp.dll`) to intercept game loading early and dynamically apply fonts and translated texts.
- **Multilingual Architecture**: Fonts (`fonts/`) and translation dictionaries (`locales/`) are cleanly separated for easy language expansion.
- **Easy Install & Uninstall**: Easily install and completely restore the original state using `install.bat` and `uninstall.bat`.

## Usage

### Installation
1. Copy all files from `bin/Release/` to your game directory (or run `install.bat`).
2. Launch the game normally via Steam. The translation will be loaded automatically.

### Uninstallation
1. Run `uninstall.bat` or delete `winhttp.dll`, `doorstop_config.ini`, and the `Translation` folder from the game directory to restore original files completely.

## Directory Structure
```text
ONE MILLION COINS/
├── README.md
├── README_EN.md
├── data/
│   └── Translation/            # Translation dictionaries & font configs
├── src/
│   └── TranslationLoader.cs    # C# loader source code
```
