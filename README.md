# 🎨 AI Portrait Forge — RimWorld Mod

> **AI-generated portraits for ANY pawn. No local GPU required.**

Generate stunning AI portraits for your colonists, animals, mechanoids, and creatures using cloud APIs. Choose from **40 art styles**, customize **9 prompt templates**, and watch your pawns come to life.

---

## ✨ Features

### 🖼️ AI Portrait Generation
- **Right-click any pawn** → "Generate Portrait" → AI creates a unique portrait
- **On-demand only** — no automatic API calls, no wasted credits
- **Automatic background removal** — every portrait gets a transparent background
- **Prompt editor** — tweak positive AND negative prompts before generating

### ☁️ 6 Cloud API Providers
| Provider | Tier | Best For |
|----------|------|----------|
| **Google Gemini** | Free tier | Recommended default |
| **Naga.ac** | Free tier | Flux/SDXL/DALL-E |
| **Pixazo** | Free tier | SDXL |
| **StabilityAI** | Paid | Professional img2img |
| **OpenRouter** | Paid | Multi-model access |
| **Generic / Custom** | Configurable | Any REST API |

### 🎨 40 Art Styles

**Classic**: Realistic, Noir, Steampunk, Cyberpunk, Pop Art, Claymation, Ukiyo-e, Pixel Art

**Anime**: Studio Ghibli, Modern Anime, Tensura (Slime), Akira Toriyama (DBZ), Eiichiro Oda (One Piece), Naruto

**Cartoon**: Pixar 3D, Cartoon Network 2D, Avatar/Korra, Courage, Adventure Time, Steven Universe, Simpsons/Futurama, Rick & Morty, Star Wars, Invincible

**Fantasy**: D&D RPG, Frank Frazetta, Alan Lee (Tolkien), Gerald Brom, Larry Elmore, Pathfinder RPG, Yoshitaka Amano, Final Fantasy, Mutsumi Inomata (Tales), Ricardo Manga

**Comic**: Western Comic Book, Erica Awano, Turma da Mônica, Moebius

### 🐾 All Pawn Types Supported
- **Humans** — 17 dynamic placeholders (age, body type, mood, personality, traits, health, implants...)
- **Animals** — mammals, birds, reptiles
- **Insects** — megaspiders, insectoids
- **Dragons** — from any mod
- **Aquatic** — fish, krakens, sea creatures
- **Plants / Dryads** — treants, fungoids
- **Mechanoids** — scythers, centipedes, modded mechs
- **Supernatural Entities** — undead, demons, celestials, elementals, aberrations, mutants, constructs, slimes, anomaly entities
- **Other / Unknown** — fallback for any modded creature

### 🎯 9 Customizable Prompt Templates
Each creature category has its own **positive** and **negative** prompt template, fully editable in Mod Options.

### 🎮 Pixel-Art Base Avatars
- Vanilla-style pixel art for human pawns (50+ layered sprites)
- Color-coded fallback avatars for creatures (always clickable)
- Compatible with **50+ mods** (Vanilla Expanded, Star Wars, Alpha Genes, etc.)

---

## ⚙️ Setup

1. Subscribe to **Harmony** (required dependency)
2. Get a free API key from [Google AI Studio](https://aistudio.google.com/apikey) (or Naga.ac / Pixazo)
3. Open **Mod Options → AI Portrait Forge**
4. Select your API provider → paste your key
5. Choose an art style
6. In-game: **right-click any pawn → Generate Portrait**

---

## 📸 Screenshots

| Pixel-art avatar | AI generated portrait |
|:---:|:---:|
| Base avatar shown immediately on click | Right-click → Generate → AI creates portrait |

---

## 🙏 Credits & Thanks

This mod is a fork and massive expansion of the original **"Avatar - Personas"** by **Meathax**.

**Original projects that made this possible:**
- [Avatar - Personas](https://steamcommunity.com/sharedfiles/filedetails/?id=3111373293) by **Meathax** — original pixel-art avatar system and mod compatibility
- [Harmony](https://github.com/pardeike/HarmonyRimWorld) by **Brrainz** — patching library
- [RimWorld](https://rimworldgame.com) by **Ludeon Studios**

**What changed from the original:**
- ❌ Removed local ComfyUI/SDXL pipeline (~14GB downloads, GPU required)
- ✅ Added 6 cloud API providers (no GPU, no downloads)
- ✅ 40 art styles (up from 7)
- ✅ 22 creature categories with per-category templates
- ✅ 9 customizable positive + negative prompt templates
- ✅ Full prompt editor (positive + negative, both editable)
- ✅ Automatic background removal (transparent PNG output)
- ✅ API provider selector + key management in UI
- ✅ 100% on-demand generation (no auto API calls)
- ✅ Expanded mod compatibility

---

## 📦 Build

```bash
dotnet restore Source/Avatar.csproj
dotnet build Source/Avatar.csproj -v:q -clp:summary
```

Output: `1.6/Assemblies/Avatar.dll`

---

**License**: Same as original project.
