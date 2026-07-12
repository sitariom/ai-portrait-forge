# SPEC: Suporte a Retratos para Animais e Criaturas

> **Status**: Especificação Técnica | **Versão**: 1.0 | **Data**: 2026-07-11
> 
> Expansão do mod "Avatar - Personas" para suportar geração de retratos
> pixel-art + IA para pawns não-humanoides (animais, mechanoids, insetos, dryads).

---

## 1. Visão Geral

### 1.1 Problema Atual

O mod filtra **explicitamente** todos os pawns não-humanoides em 5 pontos:

```csharp
// AutoPortraitGenerator.cs
if (!pawn.RaceProps.Humanlike) { return; }  // 5 ocorrências

// AvatarMod.cs (UIPatch)
if (pawn != null && pawn.RaceProps.Humanlike) // gate de renderização

// QuestWindowPatch
pawnsArrive.pawns?.Where(p => p.RaceProps.Humanlike) // filtro de quests
```

Todo o pipeline de renderização pixel-art é antropocêntrico (cabelo, barba, roupas, traços faciais).

### 1.2 Objetivo

Permitir que **qualquer pawn** (animal, mechanoid, inseto, dryad) receba:
1. Um **avatar pixel-art** simplificado (baseado na textura vanilla + cor)
2. Um **retrato IA** gerado via API cloud com prompts específicos para criaturas

### 1.3 Escopo

| Categoria | Incluído? | Notas |
|-----------|:---------:|-------|
| Animais selvagens | ✅ | `Intelligence.Animal` |
| Animais domesticados | ✅ | `Faction.OfPlayerSilentFail` |
| Mechanoids | ✅ | `RaceProps.IsMechanoid` |
| Insetos | ✅ | `RaceProps.Insect` |
| Dryads | ✅ | `RaceProps.Dryad` |
| Entidades Anomaly | ⚠️ P2 | `RaceProps.IsAnomalyEntity` |
| Animais de outros mods | ✅ | Usam o mesmo sistema |

---

## 2. Sistema de Classificação Universal de Pawns

### 2.1 Princípio: Cobertura Total

O sistema garante que **100% dos pawns** — vanilla, DLC ou de qualquer mod —
sejam classificados em uma categoria com prompt e renderização apropriados.
Nenhum pawn fica sem tratamento.

### 2.2 Enum de Categorias (Expandido)

```csharp
public enum PawnPortraitCategory
{
    // === Categorias vanilla / DLC ===
    Humanlike       = 0,   // Pipeline completo pixel-art (existente)
    Animal          = 1,   // Mamíferos, répteis, aves, peixes...
    Mechanoid       = 2,   // Robôs, drones, autômatos
    Insect          = 3,   // Insetoides, aracnídeos, artrópodes
    Dryad           = 4,   // Criaturas vegetais simbióticas
    
    // === Categorias para mods (classificação por características) ===
    Undead          = 10,  // Zumbis, esqueletos, liches, wraiths, fantasmas
    Elemental       = 11,  // Fogo, água, terra, ar, gelo, raio...
    Demon           = 12,  // Demônios, diabos, entidades infernais
    Celestial       = 13,  // Anjos, deuses, titãs, seres divinos
    Dragon          = 14,  // Dragões, wyverns, drakes, serpentes aladas
    Construct       = 15,  // Golems, estátuas vivas, armaduras animadas
    Slime           = 16,  // Gelatinosos, oozes, amorfos, plasmoides
    Aberration      = 17,  // Aberrações lovecraftianas, horrores cósmicos
    Mutant          = 18,  // Mutantes radioativos, abominações genéticas
    Plant           = 19,  // Treants, mandrágoras, fungos sencientes
    Aquatic         = 20,  // Peixes, monstros marinhos, criaturas abissais
    AnomalyEntity   = 21,  // Entidades do DLC Anomaly (shamblers, revenants...)
    
    // === Fallback universal ===
    Other           = 99   // Qualquer coisa não coberta acima
}
```

### 2.3 Sistema de Classificação em 4 Camadas

```csharp
public static PawnPortraitCategory ClassifyPawn(Pawn pawn)
{
    // ================================================================
    // CAMADA 1: API padrão do RimWorld (detecção determinística)
    // ================================================================
    
    if (pawn.RaceProps.Humanlike)
        return PawnPortraitCategory.Humanlike;
    
    if (pawn.RaceProps.IsMechanoid)
        return PawnPortraitCategory.Mechanoid;
    
    if (pawn.RaceProps.Dryad)
        return PawnPortraitCategory.Dryad;
    
    if (pawn.RaceProps.Insect)
        return PawnPortraitCategory.Insect;
    
    #if ANOMALY
    if (pawn.RaceProps.IsAnomalyEntity || pawn.IsShambler)
        return PawnPortraitCategory.AnomalyEntity;
    #endif
    
    // ================================================================
    // CAMADA 2: CreaturePromptDef XML (classificação explícita por mod)
    //   Modders podem registrar: <category>Demon</category>
    //   Isso permite que QUALQUER mod classifique suas criaturas
    // ================================================================
    
    var def = ModCreatureRegistry.GetDefForKind(pawn.kindDef.defName);
    if (def != null && def.category != PawnPortraitCategory.Animal)
        return def.category;
    
    // ================================================================
    // CAMADA 3: Detecção por palavras-chave no nome/raça
    //   Analisa defName, label, e BodyDef para inferir categoria
    // ================================================================
    
    if (pawn.RaceProps.Animal)
    {
        var inferred = InferCategoryFromKeywords(pawn);
        if (inferred != PawnPortraitCategory.Animal)
            return inferred;
        return PawnPortraitCategory.Animal;
    }
    
    // ================================================================
    // CAMADA 4: Fallback — analisa qualquer pawn não-classificado
    //   Usa keywords mesmo para tipos não-Animal
    // ================================================================
    
    var final = InferCategoryFromKeywords(pawn);
    return final;
}
```

### 2.4 Detector por Palavras-Chave (Cobre 100% dos mods)

```csharp
private static PawnPortraitCategory InferCategoryFromKeywords(Pawn pawn)
{
    // Constrói um "perfil" textual do pawn para análise
    string profile = BuildPawnProfile(pawn).ToLowerInvariant();
    
    // Dragões (verificar primeiro — muitos mods os classificam como Animal)
    if (MatchesAny(profile, 
        "dragon", "wyvern", "drake", "wyrm", "dragonkin", "draconic",
        "serpent_wings", "fire_breath", "dragonborn"))
        return PawnPortraitCategory.Dragon;
    
    // Mortos-vivos
    if (MatchesAny(profile,
        "zombie", "skeleton", "undead", "lich", "wraith", "ghost",
        "specter", "spectre", "banshee", "revenant", "necromancer",
        "bonewalker", "death_knight", "wight", "ghoul"))
        return PawnPortraitCategory.Undead;
    
    // Demônios
    if (MatchesAny(profile,
        "demon", "devil", "fiend", "imp", "succubus", "incubus",
        "hellspawn", "daemon", "balrog", "pit_fiend", "hellhound",
        "infernal", "abyssal", "nether"))
        return PawnPortraitCategory.Demon;
    
    // Celestiais
    if (MatchesAny(profile,
        "angel", "celestial", "divine", "god", "titan", "deity",
        "seraph", "cherub", "archon_", "empyrean", "radiant",
        "holy", "sacred", "light_being", "demigod"))
        return PawnPortraitCategory.Celestial;
    
    // Elementais
    if (MatchesAny(profile,
        "elemental", "fire_element", "water_element", "earth_element",
        "air_element", "ice_element", "lightning_element", "magma",
        "storm_", "ifrit", "sylph", "gnome", "undine"))
        return PawnPortraitCategory.Elemental;
    
    // Construtos
    if (MatchesAny(profile,
        "golem", "construct", "automaton", "clockwork", "animated_armor",
        "living_statue", "stone_guardian", "warforged", "homunculus",
        "effigy", "totem"))
        return PawnPortraitCategory.Construct;
    
    // Aberrações / Horror cósmico
    if (MatchesAny(profile,
        "eldritch", "cthulhu", "lovecraft", "shoggoth", "mi-go",
        "aberation", "cosmic_horror", "void_", "fleshbeast",
        "nightgaunt", "deep_one", "yith", "outer_god",
        "shambler", "amorphous_horror", "star_spawn"))
        return PawnPortraitCategory.Aberration;
    
    // Mutantes
    if (MatchesAny(profile,
        "mutant", "mutated", "abomination", "freak", "chimera",
        "genetic_horror", "flesh_golem", "hybrid_abomination",
        "radioactive", "irradiated", "fleshbeast"))
        return PawnPortraitCategory.Mutant;
    
    // Plantas sencientes
    if (MatchesAny(profile,
        "treant", "ent", "mandrake", "myconid", "fungoid",
        "plant_creature", "carnivorous_plant", "spore_", "fungal_",
        "mushroom_creature", "dryad_", "spriggan"))
        return PawnPortraitCategory.Plant;
    
    // Slimes / Gelatinosos
    if (MatchesAny(profile,
        "slime", "ooze", "gelatinous", "jelly", "blob", "plasmoid",
        "amorph", "pudding", "sludge"))
        return PawnPortraitCategory.Slime;
    
    // Aquáticos
    if (MatchesAny(profile,
        "fish", "shark", "whale", "kraken", "leviathan", "squid",
        "octopus", "sea_serpent", "merfolk", "mermaid", "siren",
        "nautilus", "crustacean", "deep_sea", "abyssal_fish",
        "aquatic", "marine", "ocean"))
        return PawnPortraitCategory.Aquatic;
    
    // Fallback padrão
    if (pawn.RaceProps.Animal)
        return PawnPortraitCategory.Animal;
    
    return PawnPortraitCategory.Other;
}

private static string BuildPawnProfile(Pawn pawn)
{
    var sb = new System.Text.StringBuilder();
    
    // Nome do PawnKind (ex: "AA_Gallatross")
    if (pawn.kindDef != null)
    {
        sb.AppendLine(pawn.kindDef.defName.ToLowerInvariant());
        sb.AppendLine(pawn.kindDef.label?.ToLowerInvariant() ?? "");
        if (pawn.kindDef.race != null)
            sb.AppendLine(pawn.kindDef.race.defName.ToLowerInvariant());
    }
    
    // Nome da raça
    if (pawn.RaceProps?.AnyPawnKind?.race != null)
    {
        sb.AppendLine(pawn.RaceProps.AnyPawnKind.race.defName.ToLowerInvariant());
        sb.AppendLine(pawn.RaceProps.AnyPawnKind.race.label?.ToLowerInvariant() ?? "");
    }
    
    // BodyDef (partes do corpo podem revelar tipo)
    if (pawn.RaceProps?.body != null)
    {
        sb.AppendLine(pawn.RaceProps.body.defName.ToLowerInvariant());
        foreach (var part in pawn.RaceProps.body.AllParts)
            sb.AppendLine(part.def.defName.ToLowerInvariant());
    }
    
    // FleshType (Mechanoid, Insect, Normal...)
    if (pawn.RaceProps?.fleshType != null)
        sb.AppendLine(pawn.RaceProps.fleshType.defName.ToLowerInvariant());
    
    return sb.ToString();
}

private static bool MatchesAny(string profile, params string[] keywords)
{
    foreach (string kw in keywords)
        if (profile.Contains(kw))
            return true;
    return false;
}
```

### 2.5 Tabela de Cobertura por Tipo de Mod

| Universo do Mod | Exemplos de Criaturas | Categoria Detectada | Método |
|-----------------|----------------------|---------------------|--------|
| **Fantasia medieval** | Dragões, golems, elementais | `Dragon`, `Construct`, `Elemental` | Keywords |
| **Horror cósmico** | Shoggoths, Deep Ones, Mi-Go | `Aberration` | Keywords |
| **Mitologia** | Minotauros, quimeras, hidras | `Animal` + traits | API padrão |
| **Necromancia** | Esqueletos, liches, wraiths | `Undead` | Keywords |
| **Demônios** | Diabos, succubi, hellhounds | `Demon` | Keywords |
| **Fallout** | Deathclaws, mutantes | `Mutant`, `Animal` | Keywords |
| **Warhammer 40k** | Tyranids, daemons | `Aberration`, `Demon` | Keywords |
| **Pokémon** | Criaturas elementais | `Elemental`, `Animal` | Keywords |
| **Star Wars** | Rancors, sarlaccs | `Animal` + traits | API padrão |
| **Subnautica** | Leviatãs, peixes abissais | `Aquatic` | Keywords |
| **Criatura desconhecida** | Qualquer coisa | `Other` → 3-layer fallback | Fallback |

---

## 3. Pipeline de Renderização para Animais

### 3.1 Estratégia

**Não** tentaremos replicar o pipeline humano para animais (exigiria milhares de sprites). Em vez disso, usamos uma abordagem em duas camadas:

#### Camada A: Avatar Pixel-Art Simplificado (fallback)
- Extrai a textura vanilla do animal via `pawn.Drawer.renderer`
- Aplica recorte e redimensionamento (máx 128x128)
- Aplica cor de fundo neutra
- Resultado: ícone do animal em pixel-art derivado da textura do jogo

#### Camada B: Retrato IA (preferencial)
- Quando disponível, substitui o avatar simplificado
- Gerado via API com prompt específico para a espécie

### 3.2 Novo Método: `RenderAnimalAvatar()`

```csharp
// AvatarManager.cs — novo método
private Texture2D RenderAnimalAvatar()
{
    int width = 80;
    int height = 80;
    
    // 1. Obtém a textura vanilla do animal
    RenderTexture active = RenderTexture.active;
    RenderTexture rt = RenderTexture.GetTemporary(width, height);
    RenderTexture.active = rt;
    
    // 2. Renderiza o pawn via sistema vanilla
    //    Usa PawnRenderer.RenderPawnAt() ou similar
    Vector2 drawSize = pawn.Drawer.renderer.graphics.drawSize;
    // ... render logic ...
    
    // 3. Copia para Texture2D
    Texture2D result = new Texture2D(width, height);
    result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
    result.Apply();
    
    RenderTexture.ReleaseTemporary(rt);
    RenderTexture.active = active;
    return result;
}
```

### 3.3 Modificação: `GetAvatar()` — dispatch por categoria

```csharp
public Texture2D GetAvatar(bool allowStatic = true)
{
    // Tenta textura IA estática primeiro (igual atual)
    if (allowStatic)
    {
        TryGetStaticTexture();
        if (staticTexture != null)
            return staticTexture;
    }
    
    // NOVO: dispatch por categoria
    PawnPortraitCategory category = ClassifyPawn(pawn);
    
    if (category == PawnPortraitCategory.Humanlike)
    {
        // Pipeline existente (RenderAvatar)
        if (avatar == null || updateQueued)
            avatar = RenderAvatar();
    }
    else
    {
        // NOVO: pipeline simplificado para animais/mechs/etc
        if (avatar == null || updateQueued)
            avatar = RenderAnimalAvatar();
    }
    
    return avatar;
}
```

### 3.4 Assets Mínimos Necessários

Para a Camada A, **zero novos assets** — usamos as texturas vanilla do jogo.

Para ícones de "pending" / "não suportado":
- `Assets/UI/AnimalPending.png` — ícone 40×40 "geração pendente"
- `Assets/UI/MechPending.png` — ícone 40×40 "geração pendente"

---

## 4. Sistema de Prompts para Criaturas

### 4.1 Estrutura do Prompt Animal

```csharp
// Novo método: AvatarManager.GetAnimalPrompts()
public string GetAnimalPrompts()
{
    PawnKindDef kind = pawn.kindDef;
    
    // Template base configurável
    string template = mod.settings.aiGenAnimalPreamble;
    // Default: "full body portrait, {species}, {age}, {size}, {gender}, {color}"
    
    string result = template
        .Replace("{species}", GetSpeciesDescription(kind))
        .Replace("{age}", GetAnimalAgeDescription())
        .Replace("{size}", GetSizeDescription())
        .Replace("{gender}", GetGenderDescription())
        .Replace("{color}", GetColorDescription());
    
    // Detalhes específicos
    List<string> details = new List<string>();
    
    if (pawn.ageTracker.CurLifeStage.shearable)
        details.Add("woolly coat");
    if (pawn.RaceProps.predator)
        details.Add("predator, sharp teeth");
    if (pawn.RaceProps.hasHorns) // precisa verificar BodyDef
        details.Add("prominent horns");
    
    if (details.Count > 0)
        result += ", " + string.Join(", ", details);
    
    return result;
}
```

### 4.2 Descrição de Espécie

```csharp
private string GetSpeciesDescription(PawnKindDef kind)
{
    // Usa o label do PawnKindDef como base
    string label = kind.label.ToLower();
    
    // Mapeamento de espécies comuns para termos mais descritivos
    // (opcional — pode ser expandido via XML Defs)
    if (kind.race.race.animalType == AnimalType.Canine)
        return label + ", canine";
    // ... outros mapeamentos ...
    
    return label;
}
```

### 4.3 Novo Def XML: `CreaturePromptDef`

```xml
<!-- Defs/CreaturePrompts.xml -->
<Defs>
    <Avatar.CreaturePromptDef>
        <defName>Wolf_Timber</defName>
        <kindDef>Wolf_Timber</kindDef>
        <category>Animal</category>
        <prompt>timber wolf, gray fur, amber eyes, powerful build</prompt>
    </Avatar.CreaturePromptDef>
</Defs>
```

```csharp
// DataTypes.cs — novo tipo
public class CreaturePromptDef : Def
{
    public string kindDef;                      // PawnKindDef.defName
    public string prompt;                       // Descrição para IA
    public PawnPortraitCategory category = PawnPortraitCategory.Other; // Classificação explícita
}
```

### 4.4 Configurações Novas em AvatarSettings

```csharp
// Prompt base para animais
public string aiGenAnimalPreamble = 
    "full body portrait, {species}, {age}, {size}, {gender}, {color}, "
    + "natural outdoor lighting, detailed fur texture";
public string aiGenAnimalPreambleDefault = 
    "full body portrait, {species}, {age}, {size}, {gender}, {color}, "
    + "natural outdoor lighting, detailed fur texture";
```

---

## 5. Modificações no Sistema de Fila

### 5.1 AutoPortraitGenerator — Remover Gates Humanlike

**Arquivo**: `AutoPortraitGenerator.cs`

#### EnqueuePawn() — linha 77
```csharp
// ANTES:
if (!pawn.RaceProps.Humanlike) { return; }

// DEPOIS:
// Sem restrição de raça — qualquer pawn pode ser enfileirado
// (o sistema de prompts trata cada categoria apropriadamente)
```

#### EnqueueOnDemand() — linha 144
```csharp
// ANTES:
if (!pawn.RaceProps.Humanlike) { return; }

// DEPOIS:
// Removido
```

#### MapComponentUpdate() — linha 250
```csharp
// ANTES:
if (pawn != null && !pawn.Destroyed && pawn.RaceProps.Humanlike)

// DEPOIS:
if (pawn != null && !pawn.Destroyed)
```

#### RegenerateMissingForAllColonists() — linha 291
```csharp
// ANTES:
if (!p.RaceProps.Humanlike) continue;

// DEPOIS:
// Removido — agora regenera para todos os pawns da facção do jogador
```

### 5.2 ProcessPawn() — Adaptar para Animais

```csharp
private void ProcessPawn(Pawn pawn)
{
    try
    {
        AvatarManager manager = new AvatarManager();
        manager.SetPawn(pawn);
        manager.SetBGColor(new Color(0, 0, 0, 0));
        manager.SetCheckDowned(false);

        // NOVO: dispatch por categoria
        string prompts;
        bool isCreature = !pawn.RaceProps.Humanlike;
        
        if (isCreature)
            prompts = manager.GetAnimalPrompts();
        else
            prompts = manager.GetPrompts();
        
        if (string.IsNullOrEmpty(prompts))
        {
            AvatarMod.UnmarkPending(pawn.thingIDNumber);
            AvatarMod.UnmarkAutoGen(pawn.thingIDNumber);
            return;
        }

        string imagePath = manager.SaveToStaticPortrait();
        string outputPath = imagePath;
        int pawnId = pawn.thingIDNumber;
        string pawnLabel = pawn.LabelShortCap;
        DateTime startedUtc = DateTime.UtcNow;

        // NOVO: passa isCreature = true para animais
        ApiClient.GeneratePortraitAsync(
            imagePath, prompts, outputPath,
            (success, error) =>
            {
                if (success)
                {
                    double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                    AvatarMod.ClearFailedAttempts(pawnId);
                }
                else
                {
                    int attempts = AvatarMod.RecordFailedAttempt(pawnId);
                    if (attempts < AvatarMod.MaxRetryAttempts)
                        AvatarMod.UnmarkAutoGen(pawnId);
                    else
                        Log.Warning("Avatar: " + pawnLabel + " hit " 
                            + AvatarMod.MaxRetryAttempts + " failed API retries");
                }
                AvatarMod.UnmarkPending(pawnId);
            },
            startedUtc,
            isCreature: isCreature  // NOVO parâmetro
        );
    }
    catch (Exception ex)
    {
        Log.Warning("Avatar: Auto-generation failed for " 
            + pawn.LabelShort + ": " + ex.Message);
        int attemptsNow = AvatarMod.RecordFailedAttempt(pawn.thingIDNumber);
        if (attemptsNow < AvatarMod.MaxRetryAttempts)
            AvatarMod.UnmarkAutoGen(pawn.thingIDNumber);
        AvatarMod.UnmarkPending(pawn.thingIDNumber);
    }
}
```

---

## 6. Modificações na API

### 6.1 ApiClient — Ativar isCreature

O parâmetro `isCreature` já existe no `GeneratePortraitAsync` mas nunca é chamado com `true`. A modificação é apenas nos callers (ProcessPawn, GeneratePortraitImmediate, etc.).

### 6.2 Ajuste nos Providers

Para o provider **Google Gemini** e **OpenRouter**, o prompt para criaturas precisa ser adaptado — o negative prompt deve suprimir características humanas:

```csharp
// AvatarMod.cs — expandir GetFullCreatureNegativePrompt
public static string GetFullCreatureNegativePrompt(AvatarSettings s)
{
    string baseNeg = "human, humanoid, person, people, clothes, "
        + "text, watermark, signature, logo, "
        + "ugly, deformed, mutated, distorted, disfigured, "
        + "extra limbs, extra heads, multiple faces, extra eyes, "
        + "asymmetric face, bad anatomy, bad proportions, "
        + "low quality, blurry, jpeg artifacts, pixelated";
    
    string artNeg = ArtStylePrompts.GetNegativePrompt(s.artStyle);
    if (string.IsNullOrEmpty(artNeg)) return baseNeg;
    return baseNeg + ", " + artNeg;
}
```

---

## 7. Modificações na UI

### 7.1 UIPatch — Remover Gate Humanlike

```csharp
// AvatarMod.cs, UIPatch.Postfix, linha 414
// ANTES:
if (pawn != null && pawn.RaceProps.Humanlike)

// DEPOIS:
if (pawn != null)
```

### 7.2 QuestWindowPatch — Remover Filtro

```csharp
// Linhas 534-542
// ANTES:
partPawns = pawnsArrive.pawns?.Where(p => p.RaceProps.Humanlike).ToList();

// DEPOIS:
partPawns = pawnsArrive.pawns?.ToList(); // todos os pawns
```

### 7.3 Mod Options — Novos Settings

Adicionar ao `DoSettingsWindowContents`:

```csharp
listingStandard.GapLine();
listingStandard.Label((TaggedString)"Animal portraits", -1, 
    "Settings for non-humanoid pawn portraits.");
listingStandard.CheckboxLabeled(
    "Auto-generate portraits for animals", 
    ref settings.autoGenerateAnimalPortraits);
listingStandard.CheckboxLabeled(
    "Show animal avatars in colonist bar", 
    ref settings.showAnimalsInColonistBar);

listingStandard.GapLine();
listingStandard.Label((TaggedString)"Animal base prompts", -1, 
    "Added before all animal prompts.\n"
    + "{species}, {age}, {size}, {gender}, {color} will be replaced.");
settings.aiGenAnimalPreamble = listingStandard.TextEntry(
    settings.aiGenAnimalPreamble);
if (listingStandard.ButtonText("Reset animal prompts to default"))
{
    settings.aiGenAnimalPreamble = settings.aiGenAnimalPreambleDefault;
}
```

### 7.4 Right-Click Float Menu para Animais

O menu atual (`GetFloatMenu`) funciona para qualquer pawn — já oferece "Generate portrait" / "Regenerate portrait". Sem mudanças necessárias.

---

## 8. Fases de Implementação

### Fase 1: Fundação (2-3 dias)
| Tarefa | Arquivo(s) | Complexidade |
|--------|-----------|:---:|
| Criar `PawnPortraitCategory` enum e `ClassifyPawn()` | `DataTypes.cs`, `AvatarManager.cs` | Baixa |
| Remover 5 gates `Humanlike` | `AutoPortraitGenerator.cs`, `AvatarMod.cs` | Baixa |
| Criar `RenderAnimalAvatar()` com textura vanilla | `AvatarManager.cs` | Média |
| Modificar `GetAvatar()` dispatch por categoria | `AvatarManager.cs` | Média |
| Criar `GetAnimalPrompts()` + template | `AvatarManager.cs` | Média |
| Adicionar `aiGenAnimalPreamble` ao `AvatarSettings` | `AvatarMod.cs` | Baixa |
| Modificar `ProcessPawn` para `isCreature` | `AutoPortraitGenerator.cs` | Baixa |
| Ativar `isCreature` nos callers de API | `AvatarManager.cs` | Baixa |

### Fase 2: Polimento (1-2 dias)
| Tarefa | Arquivo(s) | Complexidade |
|--------|-----------|:---:|
| Criar `AnimalPromptDef` + XML Defs | `DataTypes.cs`, `Defs/AnimalPrompts.xml` | Baixa |
| Melhorar `GetFullCreatureNegativePrompt` | `AvatarMod.cs` | Baixa |
| Adicionar settings UI para animais | `AvatarMod.cs` | Média |
| Remover filtros em `QuestWindowPatch` | `AvatarMod.cs` | Baixa |
| Criar ícones pending para animais | `Assets/UI/` | Baixa |
| Testar com animais vanilla (wolf, muffalo, etc.) | — | Média |

### Fase 3: Cobertura Completa (3-5 dias)
| Tarefa | Arquivo(s) | Complexidade |
|--------|-----------|:---:|
| Suporte a mechanoids (prompts específicos) | `AvatarManager.cs` | Média |
| Suporte a insetos | `AvatarManager.cs` | Baixa |
| Suporte a dryads | `AvatarManager.cs` | Baixa |
| Suporte a entidades Anomaly (shamblers, revenants, etc.) | `AvatarManager.cs`, `DataTypes.cs` | Média |
| Expandir `AnimalPromptDef` para 50+ espécies | `Defs/AnimalPrompts.xml` | Alta |
| Criar `AnomalyPromptDef` para entidades de horror | `Defs/AnomalyPrompts.xml` | Média |
| Otimizar `RenderAnimalAvatar` performance | `AvatarManager.cs` | Média |
| Testes com mods de criaturas populares | — | Alta |
| Testes com entidades Anomaly | — | Média |

### Fase 4: Ecossistema de Mods (2-3 dias)
| Tarefa | Arquivo(s) | Complexidade |
|--------|-----------|:---:|
| Criar `ModCreatureRegistry` para descoberta dinâmica de defs | `ModCompatibility.cs` | Alta |
| Criar sistema de prompts em cascata (espécie → gênero → reino) | `AvatarManager.cs` | Média |
| Detectar Alpha Animals, GeneticRim, Call of Cthulhu, etc. | `ModCompatibility.cs` | Baixa |
| Criar prompts para criaturas lovecraftianas (horror cósmico) | `Defs/AnomalyPrompts.xml` | Média |
| Criar prompts para criaturas biome-específicas | `Defs/AnimalPrompts.xml` | Média |
| Testes com Alpha Animals (100+ criaturas) | — | Alta |
| Testes com mods de horror (Cthulhu, Rim of Madness) | — | Média |

---

## 9. Suporte a Criaturas de Mods e Anomalias

### 9.1 Princípio Fundamental: Zero Configuração para Mods

A arquitetura foi projetada para que **qualquer criatura de qualquer mod funcione sem configuração adicional**. O segredo está em 3 camadas de fallback:

```
Criatura de mod é detectada
        │
        ▼
┌─ Camada 1: PromptRegistry (específico) ─────────────┐
│  Busca defName exato em AnimalPromptDef              │
│  Ex: "AA_Gallatross" → "colossal gentle giant,       │
│       six-eyed alien herbivore, thick armored skin"  │
└─ Match encontrado? ──────────────────────────────────┘
        │ NÃO
        ▼
┌─ Camada 2: CategoriaPrompt (por categoria) ──────────┐
│  Usa ClassifyPawn() para determinar o tipo base       │
│  Ex: IsMechanoid → template de mechanoid             │
│       Animal + predator → template de predador       │
│       IsAnomalyEntity → template de horror           │
└──────────────────────────────────────────────────────┘
        │
        ▼
┌─ Camada 3: Prompt genérico (fallback universal) ─────┐
│  Extrai label do PawnKindDef como descrição           │
│  Ex: "AA_ShadowSalamander" → "shadow salamander"     │
│  + template base configurável pelo usuário            │
└──────────────────────────────────────────────────────┘
```

### 9.2 Classificação de Criaturas de Mods

```csharp
public static PawnPortraitCategory ClassifyPawn(Pawn pawn)
{
    // Ordem importa: verifica primeiro os tipos mais específicos
    
    if (pawn.RaceProps.Humanlike)
        return PawnPortraitCategory.Humanlike;
    
    // Anomaly — entidades do DLC + mods de horror
    if (pawn.RaceProps.IsAnomalyEntity)
        return PawnPortraitCategory.AnomalyEntity;
    
    // Shamblers são um subtipo de AnomalyEntity mas também são humanoides
    // corrompidos. Verificar antes de Mechanoid.
    #if ANOMALY
    if (pawn.IsShambler)
        return PawnPortraitCategory.AnomalyEntity;
    #endif
    
    if (pawn.RaceProps.IsMechanoid)
        return PawnPortraitCategory.Mechanoid;
    
    if (pawn.RaceProps.Dryad)
        return PawnPortraitCategory.Dryad;
    
    if (pawn.RaceProps.Insect)
        return PawnPortraitCategory.Insect;
    
    if (pawn.RaceProps.Animal)
        return PawnPortraitCategory.Animal;
    
    // Fallback universal: qualquer pawn com gráfico renderizável
    // (pega mods que criam tipos completamente novos)
    if (pawn.Drawer?.renderer?.graphics != null)
        return PawnPortraitCategory.Other;
    
    return PawnPortraitCategory.Other;
}
```

### 9.3 Detecção de Mods Específicos

```csharp
// ModCompatibility.cs — novas detecções
public static class ModCreatureRegistry
{
    // Mods conhecidos com criaturas não-standard
    public static bool AlphaAnimals_Loaded = 
        ModsConfig.IsActive("sarg.alphaanimals");
    public static bool GeneticRim_Loaded = 
        ModsConfig.IsActive("sarg.geneticrim");
    public static bool CallOfCthulhu_Loaded = 
        ModsConfig.IsActive("Jecrell.jecstools");
    public static bool RimOfMadness_Loaded = 
        ModsConfig.IsActive("si_ii.rimofmadness");
    public static bool VFE_Insectoids_Loaded = 
        ModsConfig.IsActive("OskarPotocki.VFE.Insectoid");
    public static bool Megafauna_Loaded = 
        ModsConfig.IsActive("sarg.megafauna");
    public static bool Dinosauria_Loaded = 
        ModsConfig.IsActive("sarg.dinosauria");
    public static bool Moonjelly_Race_Loaded = 
        ModsConfig.IsActive("moonjelly.moonjellyrace");
    
    // Cache de prompts por defName para lookup O(1)
    private static Dictionary<string, CreaturePromptDef> defCache;
    
    static ModCreatureRegistry()
    {
        defCache = new Dictionary<string, CreaturePromptDef>();
        foreach (CreaturePromptDef def in DefDatabase<CreaturePromptDef>.AllDefs)
        {
            if (!string.IsNullOrEmpty(def.kindDef))
                defCache[def.kindDef] = def;
        }
    }
    
    public static CreaturePromptDef GetDefForKind(string kindDefName)
    {
        return defCache.TryGetValue(kindDefName, out CreaturePromptDef def) 
            ? def : null;
    }
    
    public static string GetPromptForKind(string kindDefName)
    {
        var def = GetDefForKind(kindDefName);
        return def?.prompt;
    }
}
```

### 9.4 Sistema de Prompts em Cascata

```csharp
// AvatarManager.cs — GetCreaturePrompts() unificado
public string GetCreaturePrompts()
{
    PawnPortraitCategory category = ClassifyPawn(pawn);
    PawnKindDef kind = pawn.kindDef;
    
    // CAMADA 1: Prompt específico por espécie (XML Def)
    string specificPrompt = ModCreatureRegistry.GetPromptForKind(kind.defName);
    if (!string.IsNullOrEmpty(specificPrompt))
        return BuildPromptFromTemplate(category, specificPrompt);
    
    // CAMADA 2: Prompt por categoria + características detectadas
    string categoryPrompt = GetCategoryBasePrompt(category);
    string traits = DetectCreatureTraits();
    
    // CAMADA 3: Prompt genérico usando label do jogo
    string speciesName = kind.label?.ToLower() ?? kind.defName.ToLower();
    
    string template = GetPromptTemplateForCategory(category);
    return template
        .Replace("{species}", speciesName)
        .Replace("{traits}", traits)
        .Replace("{category_prompt}", categoryPrompt);
}

private string GetCategoryBasePrompt(PawnPortraitCategory category)
{
    switch (category)
    {
        case PawnPortraitCategory.AnomalyEntity:
            return "eldritch horror, unnatural anatomy, cosmic dread, "
                 + "dark atmosphere, bioluminescent, otherworldly";
        case PawnPortraitCategory.Mechanoid:
            return "mechanical, metal surface, robotic, industrial design, "
                 + "matte metal finish, sci-fi, detailed machinery";
        case PawnPortraitCategory.Insect:
            return "exoskeleton, chitin, insectoid, detailed carapace, "
                 + "compound eyes, mandibles, arthropod";
        case PawnPortraitCategory.Dryad:
            return "plant-like creature, organic, bark texture, "
                 + "symbiotic, nature spirit, glowing";
        default: // Animal + Other
            return "";
    }
}

private string DetectCreatureTraits()
{
    var traits = new List<string>();
    
    // Tamanho
    float bodySize = pawn.ageTracker.CurLifeStage.bodySizeFactor 
        * pawn.RaceProps.baseBodySize;
    if (bodySize >= 3.0) traits.Add("colossal size, giant creature");
    else if (bodySize >= 2.0) traits.Add("large size");
    else if (bodySize <= 0.4) traits.Add("tiny, diminutive");
    
    // Dieta
    if (pawn.RaceProps.predator) traits.Add("predator, sharp teeth, hunter");
    
    // Anomaly-specific
    #if ANOMALY
    if (pawn.IsShambler) traits.Add("undead, rotting flesh, shambling corpse");
    #endif
    
    // Corpo — verificar partes especiais
    if (pawn.health?.hediffSet != null)
    {
        var body = pawn.RaceProps.body;
        if (body != null)
        {
            if (body.HasPartWithTag("Wing")) traits.Add("winged, flying");
            if (body.AllParts.Any(p => p.def.defName.Contains("Horn") 
                || p.def.defName.Contains("Tusk")))
                traits.Add("prominent horns");
            if (body.AllParts.Any(p => p.def.defName.Contains("Tail")))
                traits.Add("long tail");
        }
    }
    
    return traits.Count > 0 ? string.Join(", ", traits) : "";
}
```

### 9.5 Sistema de Defs Expansível por Modders

Outros mods podem fornecer prompts para suas criaturas através de patches XML:

```xml
<!-- Exemplo: um mod de criaturas pode adicionar isto via Patch XML -->
<Patch>
    <Operation Class="PatchOperationAdd">
        <xpath>Defs</xpath>
        <value>
            <!-- Prompts para Alpha Animals -->
            <Avatar.AnimalPromptDef>
                <defName>AA_Gallatross</defName>
                <kindDef>AA_Gallatross</kindDef>
                <prompt>colossal six-eyed gentle giant, thick armored hide, 
                    bioluminescent markings, alien herbivore, elephant-like 
                    build, mysterious peaceful creature</prompt>
            </Avatar.AnimalPromptDef>
            
            <Avatar.AnimalPromptDef>
                <defName>AA_Feralisk</defName>
                <kindDef>AA_Feralisk</kindDef>
                <prompt>giant arachnid predator, eight spindly legs, 
                    multiple eyes, chitinous armor, venomous fangs, 
                    web-spinner, aggressive stance</prompt>
            </Avatar.AnimalPromptDef>
            
            <!-- Prompts para Call of Cthulhu -->
            <Avatar.AnimalPromptDef>
                <defName>COC_DeepOne</defName>
                <kindDef>COC_DeepOne</kindDef>
                <prompt>humanoid amphibian creature, fish-like face, 
                    bulging unblinking eyes, scaly green-grey skin, 
                    webbed hands, deep one, Lovecraftian horror, 
                    submerged ancient ruins background</prompt>
            </Avatar.AnimalPromptDef>
            
            <!-- Prompts para entidades Anomaly -->
            <Avatar.CreaturePromptDef>
                <defName>Revenant</defName>
                <kindDef>Revenant</kindDef>
                <category>AnomalyEntity</category>
                <prompt>ethereal ghostly apparition, translucent form, 
                    floating, tattered spectral robes, cold blue glow, 
                    phasing through reality, nightmare entity</prompt>
            </Avatar.CreaturePromptDef>
            
            <!-- Exemplo com categoria explícita para mod de mitologia -->
            <Avatar.CreaturePromptDef>
                <defName>Myth_Dragon_Ancient</defName>
                <kindDef>Myth_Dragon_Ancient</kindDef>
                <category>Dragon</category>
                <prompt>ancient red dragon, massive scaled wings, 
                    molten fire seeping from nostrils, horns like crowns,
                    obsidian claws, mountain of treasure background</prompt>
            </Avatar.CreaturePromptDef>
            
            <Avatar.CreaturePromptDef>
                <defName>FO4_Deathclaw</defName>
                <kindDef>FO4_Deathclaw</kindDef>
                <category>Mutant</category>
                <prompt>massive irradiated mutant reptile, horns, 
                    thick armored hide, razor claws, post-apocalyptic wasteland</prompt>
            </Avatar.CreaturePromptDef>
        </value>
    </Operation>
</Patch>
```

### 9.6 Entidades Anomaly — Pipeline Especial

O DLC Anomaly introduz entidades que desafiam a classificação tradicional.
Algumas são humanoides corrompidos (shamblers), outras são completamente
alienígenas (revenants, noctols, fleshbeasts).

```csharp
private string GetPromptTemplateForCategory(PawnPortraitCategory category)
{
    switch (category)
    {
        case PawnPortraitCategory.AnomalyEntity:
            return mod.settings.aiGenAnomalyPreamble;
        case PawnPortraitCategory.Mechanoid:
            return mod.settings.aiGenMechPreamble;
        case PawnPortraitCategory.Insect:
        case PawnPortraitCategory.Dryad:
        case PawnPortraitCategory.Animal:
            return mod.settings.aiGenAnimalPreamble;
        case PawnPortraitCategory.Other:
            return mod.settings.aiGenOtherPreamble;
        default: // Humanlike
            return mod.settings.aiGenPreamble;
    }
}
```

### 9.7 Templates de Prompt por Categoria (Expansão)

```csharp
// AvatarSettings.cs — novos campos
public string aiGenAnomalyPreamble = 
    "full body portrait, {species}, {category_prompt}, {traits}, "
    + "dark atmospheric lighting, cosmic horror aesthetic, "
    + "detailed texture, eldritch, nightmarish, sinister glow";

public string aiGenMechPreamble = 
    "full body portrait, {species}, {category_prompt}, {traits}, "
    + "studio lighting on dark background, industrial, military grade, "
    + "detailed mechanical components, panel lines, scuffed metal";

public string aiGenOtherPreamble = 
    "full body portrait, {species}, {traits}, "
    + "detailed texture, natural lighting";
```

### 9.8 Prioridade de Enfileiramento para Criaturas de Mods

```csharp
// AutoPortraitGenerator.cs — EnqueuePawn modificado
public void EnqueuePawn(Pawn pawn)
{
    // ... validações iniciais ...
    
    AvatarMod.MarkAutoGen(pawnId);
    AvatarMod.MarkPending(pawnId);
    
    // NOVO: prioridade baseada na categoria
    PawnPortraitCategory category = AvatarManager.ClassifyPawn(pawn);
    
    if (pawn.Faction == Faction.OfPlayerSilentFail)
    {
        // Colonos humanoides SEMPRE em primeiro lugar
        if (category == PawnPortraitCategory.Humanlike)
            colonyQueue.Enqueue(pawn);
        else
            // Animais domesticados vão para otherQueue
            // (não bloqueiam retratos de colonos)
            otherQueue.Enqueue(pawn);
    }
    else
    {
        otherQueue.Enqueue(pawn);
    }
}
```

---

## 10. Compatibilidade com Mods Específicos

### 10.1 Mods de Criaturas — Matriz de Testes

| Mod | Criaturas | Categoria detectada | Prompt |
|-----|-----------|-------------------|--------|
| **Alpha Animals** | Gallatross, Feralisk, Meadow Ave, etc. | `Animal` | Label do jogo + traits |
| **Genetic Rim** | Híbridos (Thrumbo+Wolf, etc.) | `Animal` | Label + traits compostos |
| **Call of Cthulhu** | Deep Ones, Mi-Go, Elder Things | `Animal` / `Other` | Prompt específico via XML |
| **Rim of Madness** | Vampiros, Lobisomens | `Humanlike` (se humanoide) | Pipeline humano normal |
| **VFE Insectoids** | Insetos expandidos | `Insect` | Template inseto |
| **Megafauna** | Mamutes, tigres-dente-sabre | `Animal` | Template mamífero grande |
| **Dinosauria** | Dinossauros | `Animal` | Template réptil |
| **Moonjelly Race** | Raça alienígena | `Humanlike` | Pipeline humano normal |
| **Android Tiers** | Androids | `Humanlike` | Pipeline humano normal |
| **Anomaly DLC** | Shamblers, Revenants, Noctols | `AnomalyEntity` | Template horror |

### 10.2 Estratégia para Criaturas Totalmente Desconhecidas

Quando um mod cria um tipo de criatura que não se encaixa em nenhuma categoria:

1. `ClassifyPawn()` retorna `Other`
2. `RenderAnimalAvatar()` usa `pawn.Drawer.renderer.graphics` (sempre funciona se o pawn é renderizável)
3. `GetCreaturePrompts()` usa o label do `PawnKindDef` como descrição
4. Template base `aiGenOtherPreamble` configurável pelo usuário
5. Se o pawn não tem gráfico renderizável → ícone "?" genérico

**Isso garante que o mod nunca crasha com criaturas desconhecidas.**

### 9.1 `DataTypes.cs` — Adições

```csharp
// Novos tipos a adicionar:

public enum PawnPortraitCategory
{
    Humanlike,
    Animal,
    Mechanoid,
    Insect,
    Dryad,
    AnomalyEntity,     // Entidades do DLC Anomaly + mods de horror
    Other              // Fallback universal
}

public class CreaturePromptDef : Def
{
    public string kindDef;                      // PawnKindDef.defName
    public string prompt;                       // Descrição para IA
    public PawnPortraitCategory category = PawnPortraitCategory.Other; // Classificação explícita (opcional)
}
```

### 9.2 `AvatarManager.cs` — Adições

**Novos métodos**:
- `public static PawnPortraitCategory ClassifyPawn(Pawn pawn)` — ~15 linhas
- `private Texture2D RenderAnimalAvatar()` — ~50 linhas
- `public string GetAnimalPrompts()` — ~30 linhas
- `private string GetSpeciesDescription(PawnKindDef kind)` — ~20 linhas
- `private string GetAnimalAgeDescription()` — ~15 linhas
- `private string GetSizeDescription()` — ~10 linhas
- `private string GetColorDescription()` — ~15 linhas

**Métodos modificados**:
- `GetAvatar()` — adicionar dispatch (linha ~1010)
- `GeneratePortraitImmediate()` — adicionar `isCreature`
- `GeneratePortraitImmediateSilent()` — adicionar `isCreature`

### 9.3 `AutoPortraitGenerator.cs` — Modificações

| Linha | Mudança |
|-------|---------|
| 77 | Remover `if (!pawn.RaceProps.Humanlike) { return; }` |
| 144 | Remover `if (!pawn.RaceProps.Humanlike) { return; }` |
| 250 | `pawn.RaceProps.Humanlike` → `true` (remover condição) |
| 264 | Atualizar mensagem de log |
| 291 | Remover `if (!p.RaceProps.Humanlike) continue;` |
| 308-365 | Modificar `ProcessPawn()` para dispatch animal |

### 9.4 `AvatarMod.cs` — Modificações

| Linha | Mudança |
|-------|---------|
| 365-368 | Expandir `GetFullCreatureNegativePrompt` |
| 414 | Remover `pawn.RaceProps.Humanlike` |
| 534-542 | Remover filtros `.Where(p => p.RaceProps.Humanlike)` |
| ~680 | Adicionar campos `autoGenerateAnimalPortraits`, `showAnimalsInColonistBar`, `aiGenAnimalPreamble` |
| ~700 | Adicionar Scribe lines |
| ~270 | Adicionar UI no `DoSettingsWindowContents` |

### 9.5 `ApiClient.cs` — Sem Modificações

O parâmetro `isCreature` já existe. Apenas os callers precisam passar `true`.

### 9.6 Novos Arquivos

| Arquivo | Conteúdo |
|---------|----------|
| `Defs/AnimalPrompts.xml` | `<Defs>` com `<Avatar.AnimalPromptDef>` para espécies comuns |
| `Assets/UI/AnimalPending.png` | Ícone 40×40 |
| `Assets/UI/MechPending.png` | Ícone 40×40 |

---

## 11. Considerações de Performance

### 10.1 RenderAnimalAvatar
- Usa `RenderTexture.GetTemporary` (pooled, não aloca)
- Tamanho máximo 128×128 (baixo custo)
- Chamado apenas quando `avatar == null` (cacheado)

### 10.2 Queue
- Animais são enfileirados em `otherQueue` (prioridade mais baixa que colonos)
- Mesmo sistema de retry budget (3 tentativas)
- Sem impacto no pipeline humano

### 10.3 API
- `isCreature` não adiciona overhead — apenas muda o prompt string
- Mesmo timeout e thread pool

---

## 12. Riscos e Mitigações

| Risco | Impacto | Mitigação |
|-------|---------|-----------|
| Textura vanilla não disponível para algumas espécies | Avatar quebrado | Fallback para ícone genérico |
| Prompt IA gera resultado ruim para criaturas | Retrato feio | `AnimalPromptDef` permite override manual |
| Performance com muitos animais no mapa | Lag | Throttling já existe (0.05s entre dispatches) |
| Conflito com mods que modificam renderização animal | Avatar errado | Usar textura vanilla via `ContentFinder` |

---

## 13. Plano de Testes

### Testes Unitários (conceituais)
- [ ] `ClassifyPawn()` retorna categoria correta para cada tipo (Humanlike, Animal, Mechanoid, Insect, Dryad, AnomalyEntity, Other)
- [ ] `GetCreaturePrompts()` não retorna vazio para nenhuma categoria
- [ ] `GetCreaturePrompts()` para `Other` usa label do PawnKindDef como fallback
- [ ] `RenderAnimalAvatar()` não crasha com pawn null/destroyed/without-graphics
- [ ] Queue processa criaturas sem crash
- [ ] `ModCreatureRegistry.GetPromptForKind()` retorna null para espécie não registrada

### Testes de Integração — Vanilla
- [ ] Wolf timber — avatar aparece no painel de inspeção
- [ ] Muffalo — retrato IA gerado com prompt animal
- [ ] Labrador retriever (domesticado) — avatar na colonist bar
- [ ] Mechanoid (scyther) — categoria Mechanoid
- [ ] Insect (megaspider) — categoria Insect
- [ ] Dryad (gauranlen) — categoria Dryad
- [ ] Shambler (Anomaly) — categoria AnomalyEntity
- [ ] Revenant (Anomaly) — categoria AnomalyEntity

### Testes de Integração — Mods Populares
- [ ] Alpha Animals — Gallatross, Feralisk, Meadow Ave (sem XML)
- [ ] Alpha Animals — com XML de prompt específico
- [ ] VFE Insectoids — insetos expandidos usam template inseto
- [ ] Call of Cthulhu — Deep One usa template Other + label
- [ ] Megafauna — mamute usa template mamífero + traits de tamanho
- [ ] Genetic Rim — híbrido usa label composto + traits

### Testes de Regressão
- [ ] Colonos humanos continuam funcionando exatamente como antes
- [ ] Fila de prioridade: colonos > animais domesticados > resto
- [ ] Retry budget funciona para criaturas (3 tentativas)
- [ ] Spinner aparece na colonist bar para animais pendentes

---

## Apêndice A: Mapeamento de Desenvolvimento por Estágio de Vida

| Estágio | defName vida | Descrição |
|---------|-------------|-----------|
| Bebê | `AnimalBaby` | 0 a ~0.2 adultez |
| Jovem | `AnimalJuvenile` | ~0.2 a ~0.5 adultez |
| Adulto | `AnimalAdult` | ~0.5+ adultez |

### Propriedades visuais animais:
- `pawn.ageTracker.CurKindLifeStage.bodyGraphicData` — textura do corpo
- `pawn.ageTracker.CurKindLifeStage.femaleGraphicData` — variante fêmea
- `pawn.Drawer.renderer.graphics.drawSize` — tamanho em world units
- `pawn.ageTracker.CurLifeStage.bodySizeFactor` — fator de escala

### Cores de animais:
- Animais não têm `pawn.story.SkinColor` ou `pawn.story.hairColor`
- A cor vem da textura vanilla + máscara (`GraphicData.color` + `maskPath`)
- `GraphicData.colorTwo` pode fornecer segunda cor (ex.: manchas)

---

## Apêndice B: Template de Prompt por Categoria

### Animal (mamífero):
```
full body portrait, {species}, {age}, {size}, {gender}, {color},
natural outdoor lighting, detailed fur texture, wildlife photography,
standing on natural ground
```

### Animal (réptil):
```
full body portrait, {species}, {age}, {size}, detailed scales,
natural lighting, wildlife photography, textured skin
```

### Animal (ave):
```
full body portrait, {species}, {age}, {size}, detailed feathers,
natural lighting, wildlife photography, avian features
```

### Mechanoid:
```
full body portrait, {species}, mechanical, metal surface, robotic,
industrial design, matte metal finish, sci-fi, detailed machinery,
studio lighting on dark background
```

### Insect:
```
full body portrait, {species}, exoskeleton, chitin, insectoid,
detailed carapace texture, compound eyes, mandibles,
natural cave lighting
```

### Dryad:
```
full body portrait, {species}, plant-like creature, organic bark texture,
symbiotic being, nature spirit, glowing bioluminescence, forest guardian,
natural forest lighting, magical atmosphere
```

### Anomaly Entity (Shambler):
```
full body portrait, {species}, undead, rotting flesh, shambling corpse,
torn clothing, vacant stare, pallid skin, horror aesthetic,
dark oppressive atmosphere, fog, dim cold lighting
```

### Anomaly Entity (Revenant):
```
full body portrait, {species}, ethereal ghostly apparition, translucent,
floating, tattered spectral robes, cold blue glow, phasing through reality,
nightmare entity, incorporeal, haunting presence
```

### Anomaly Entity (genérico):
```
full body portrait, {species}, eldritch horror, unnatural anatomy,
cosmic dread, dark atmosphere, bioluminescent, otherworldly,
Lovecraftian, nightmarish, impossible geometry
```

### Other (fallback universal):
```
full body portrait, {species}, {traits}, detailed texture,
natural lighting, centered composition on neutral background
```
