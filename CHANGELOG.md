# Changelog

All notable changes to AI Portrait Forge will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

## [1.0.3] - 2026-07-20

### Added

- **Background removal toggle** — new checkbox in Mod Settings to enable/disable automatic background removal after AI portrait generation
- **Individual background removal** — right-click a pawn → "Remove background from this portrait" runs the remover on just that pawn's existing portrait
- **Batch background removal** — Mod Settings → "Remove background from all existing portraits" processes every PNG in the portrait folder at once

### Changed

- `TextureUtil.RemoveBackground()` is now gated by `settings.removeBackground` in all 3 generation call sites (auto-generator, manual regenerate, prompt window)

## [1.0.2] - 2026-07-19

### Fixed

- Duplicate `AIGenPromptDef` definitions causing RimWorld to skip 24 prompts
  - `AIGenPrompts.xml` and `AIGenPrompts_Pollinations.xml` shared identical `<defName>` values
  - RimWorld's `DefDatabase.AddAllInMods()` skips ALL duplicates, losing both prompts silently
  - Merged `<pollinationsPrompt>` fields into base `AIGenPrompts.xml` for all 24 defs:
    10 xenotypes (Dirtmole, Genie, Hussar, Sanguophage, Neanderthal, Pigskin, 
    Impid, Waster, Yttakin, Highmate), 9 cosmetics, and 5 structural defs
  - Deleted `AIGenPrompts_Pollinations.xml` to eliminate the source of duplication

## [1.0.1] - 2026-07-19

### Fixed

- `previewImage` XML error (removed tag; no preview file exists)
- Updated Krafs.Rimworld.Ref from 1.6.4817-beta to 1.6.4871 (matches game version)

## [1.0.0] - 2026-07-19

### Added

- AI-powered portrait generation for any pawn — humans, animals, mechs, creatures
- 6 API providers at launch: Google Gemini, OpenAI, Stable Diffusion, 
  Together AI, OpenRouter, Pollinations.ai
- 40+ art styles with configurable positive/negative prompts
- 22 creature-specific prompt templates
- Automatic background removal with transparent PNG output
- API key manager with Test Connection buttons
- Pixel-art fallback avatars when AI generation fails
- Mod support for 50+ content packs
- Comprehensive settings UI for fine-tuning all aspects of generation

### Changed

- Fork rebranded from "Avatar — Personas" to "AI Portrait Forge"
- Full codebase audit, bug fixes, and cleanup
- All references updated to sitariom.aiPortraitForge package ID
