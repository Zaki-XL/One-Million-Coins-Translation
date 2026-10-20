English | [日本語 (Japanese)](README.md)

# One Million Coins Multilingual Translation System (Doorstop-based)

A non-invasive, Doorstop-based multilingual translation system for the Steam game "One Million Coins".  
Supports Japanese (default), Simplified Chinese, Korean, and French.

---

## Features
- **Zero Game File Modification**: Does not modify or overwrite any game executable, DLL, or assets (preserves game integrity 100%).
- **Early Injection**: Uses the Windows DLL proxy mechanism (Doorstop `winhttp.dll`) to intercept game initialization early, dynamically applying fonts and translations.
- **YAML Native Dictionaries**: Human-readable and editable YAML format dictionaries. Supports per-entry font scaling (`scale`) and selective toggling (`enabled`).
- **Atlas Protection & Zero-Allocation Optimization**: Prevents Unity IMGUI dynamic font atlas corruption via glyph pre-warming (`RequestCharactersInTexture`) and minimizes GC Alloc.
- **Multilingual Architecture**: Switch languages via `config.json`. Automatically selects OS system fonts (Yu Gothic UI, Microsoft YaHei, Malgun Gothic, Segoe UI, etc.) or custom TTF/OTF fonts.
- **Easy Install & Clean Uninstall**: Simply place or remove files in the game root folder to install or completely restore original game state.

---

## Usage

### Installation
1. Copy all files from `bin/Release/` (`winhttp.dll`, `doorstop_config.ini`, and `Translation/`) into your game install directory (where `One Million Coins.exe` is located).
2. Launch the game normally via Steam. The translation will be applied automatically.

### Switching Languages
Edit `"language"` in `Translation/locales/config.json`:
```json
{
  "language": "ja"
}
```
Available options:
- `"ja"`: Japanese (日本語)
- `"zh"`: Simplified Chinese (简体中文)
- `"ko"`: Korean (한국어)
- `"fr"`: French (Français)

### Uninstallation
Simply delete `winhttp.dll`, `doorstop_config.ini`, and the `Translation` folder from the game directory to completely restore the original state.

---

## Translation Dictionary (YAML) Specifications & Customization

Dictionary files are located in `Translation/locales/<language_code>.yaml` (e.g., `ja.yaml`).  
You can edit them with any text editor and restart the game to see changes immediately (no C# recompilation needed).

### 1. Section Structure
Dictionaries consist of 6 sections:
- **`meta`**: Language code and display name (e.g., `language: "ja"`, `display_name: "日本語"`).
- **`branches`**: Names and descriptions for all 7 skill tree branches (`0` to `6`).
- **`upgrades`**: Names and descriptions for the 7 base stat upgrades.
- **`items`**: Names and descriptions for all 18 items/equipment.
- **`ui`**: UI labels across Main Menu, Options, Controls, Leaderboards, dynamic stats, etc.
- **`skills`**: Names and detailed descriptions for all 238 skills.

---

### 2. Supported Entry Properties

The following properties can be configured on each entry:

| Property | Type | Default | Description |
|---|:---:|:---:|---|
| **`text`** | string | - | Translated display text (used in `ui` entries). |
| **`name`** | string | - | Display name for skills, items, branches, or upgrades. |
| **`description`** | string | - | Description text. Line breaks can be written as `\n` or YAML block scalar (`\|`). |
| **`enabled`** | bool | `true` | If set to `false`, **only this entry's translation is bypassed**, displaying original English. |
| **`scale`** | float | `1.0` | Font size scale factor (`0.01` - `1.0`). Used to prevent text overflowing UI buttons. |
| **`name_scale`** | float | `scale` value | Font size scale factor specifically for the title/name. |
| **`desc_scale`** | float | `scale` value | Font size scale factor specifically for the description. |

> [!TIP]
> **Preventing Button Overflow (`scale`)**:  
> Setting `scale: 0.8` wraps the text in Unity RichText `<size=80%>...</size>`, reducing rendered font size by 20%. Ideal for fitting longer translated words inside fixed-size buttons.

---

### 3. Syntax Styles

You can freely choose between "Single-line short syntax" and "Nested syntax" depending on your needs.

#### A. Single-line Short Syntax (When custom scale or enabled toggle is not needed)
```yaml
ui:
  "START": "Start"
  "OPTIONS": "Settings"
  "BUY": "Purchase"
```

#### B. Nested Syntax (When specifying scale or enabled)
Indent attributes with **4 spaces** under the key:
```yaml
ui:
  # Scaled to 80% to fit within button boundaries
  "Credits":
    text: "Credits"
    enabled: true
    scale: 0.8

  # Keep original English for specific debug entry
  "DEBUG_MENU":
    text: "Debug Menu"
    enabled: false

skills:
  1325:
    name: "Fast Pump"
    description: "Pump interval -3s."
    scale: 0.95
```

---

## Behavior During Game Updates
For compatibility details, risk analysis, and maintenance steps when Steam pushes game patches/updates, refer to **[VERSION_UPDATE_GUIDE.md](VERSION_UPDATE_GUIDE.md)**.

---

## Directory Structure
```text
ONE MILLION COINS/
├── README.md                   # Japanese Documentation
├── README_EN.md                # English Documentation (This file)
├── VERSION_UPDATE_GUIDE.md     # Game Update & Compatibility Guide
├── data/
│   └── Translation/
│       ├── locales/
│       │   ├── config.json     # Language selector ("language": "ja", etc.)
│       │   ├── ja.yaml         # Japanese dictionary
│       │   ├── zh.yaml         # Simplified Chinese dictionary
│       │   ├── ko.yaml         # Korean dictionary
│       │   └── fr.yaml         # French dictionary
│       └── fonts/              # Custom font directory
├── doc/
│   └── reviewer_report.md      # Quality & Security Audit Report
└── src/
    └── TranslationLoader.cs    # C# Loader Source Code
```