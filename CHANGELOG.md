# Changelog

All notable changes to AI Portrait Forge will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

### Added

- Pollinations.ai as 7th API provider: free generative AI via zimage/flux models
- `Defs/AIGenPrompts_Pollinations.xml` — natural-language xenotype prompts for 20+ 
  entries, optimized for Pollinations models (no SD weight syntax)
- Provider-aware prompt pipeline: `pollinationsPrompt` field on `AIGenPromptDef` 
  for mod authors to add Pollinations-optimized versions alongside existing SD prompts

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
