using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
#if !(v1_3 || v1_4)
using LudeonTK;
#endif
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public class AvatarMod : Mod
    {
        public AvatarSettings settings;

        public static Dictionary<string, Texture2D> cachedTextures = new ();

        public static AvatarManager mainManager = new ();
        public static Dictionary<Pawn, AvatarManager> colonistBarManagers = new ();
        public static Dictionary<Pawn, AvatarManager> questTabManagers = new ();
        // ============================================================
        // Cross-thread shared state — backed by ConcurrentDictionary so
        // Process.Exited callbacks (ThreadPool threads) can mutate these
        // while the main UI thread reads them every frame. Using a plain
        // HashSet<int> here was a latent bug: HashSet is NOT thread-safe
        // and concurrent Add/Remove during an internal resize can throw
        // or return torn results. byte is a one-byte sentinel value;
        // we only care about key presence.
        // ============================================================

        // autoGenTriggered: pawns we've already tried to auto-generate for
        // (either succeeded → portrait file on disk, OR in-flight). Stops
        // the periodic safety scan from re-enqueueing the same pawn forever.
        public static ConcurrentDictionary<int, byte> autoGenTriggered = new ConcurrentDictionary<int, byte>();

        // pendingPortraitPawnIds: pawns currently in the queue OR mid-process.
        // ColonistBarPatch's spinner Postfix reads this every frame; the
        // ConcurrentDictionary tolerates Exited-handler mutations on a
        // ThreadPool thread without throwing.
        public static ConcurrentDictionary<int, byte> pendingPortraitPawnIds = new ConcurrentDictionary<int, byte>();

        // failedAttempts: per-pawn retry counter. After MaxRetryAttempts hard
        // failures (subprocess exit != 0 OR exit 0 with no output file), we
        // STOP auto-retrying that pawn — a permanently-failing pawn would
        // otherwise cycle through the 20s safety scan forever. Cleared on
        // manual right-click → Regenerate so the user can override.
        public static ConcurrentDictionary<int, int> failedAttempts = new ConcurrentDictionary<int, int>();
        public const int MaxRetryAttempts = 3;

        // hiddenPawns: "don't draw the main inspect-pane avatar for this pawn".
        // Main-thread only (toggled via right-click float menu, read by UIPatch),
        // so HashSet is fine. Persisted across save/load via AvatarGameComponent.
        public static HashSet<int> hiddenPawns = new HashSet<int>();

        // === Convenience accessors for the concurrent sets ===
        // Use these from new code instead of touching the dicts directly —
        // they document intent ("mark this pawn pending") and centralize the
        // byte-sentinel boilerplate.
        // pendingCount mirrors pendingPortraitPawnIds.Count but as a plain
        // volatile int. ConcurrentDictionary.Count acquires ALL internal
        // segment locks on every read, and the spinner Postfix reads the
        // count once per colonist per IMGUI pass as its hot-path early-out.
        // Keeping a manually-maintained counter turns that into a single
        // field read. Only MarkPending/UnmarkPending mutate the dict, so
        // bumping the counter exactly when TryAdd/TryRemove succeed keeps
        // them in lockstep. Interlocked guards against the Process.Exited
        // ThreadPool callbacks racing the main thread.
        private static int pendingCount = 0;
        public static bool MarkPending(int id)
        {
            if (pendingPortraitPawnIds.TryAdd(id, 0))
            {
                System.Threading.Interlocked.Increment(ref pendingCount);
                return true;
            }
            return false;
        }
        public static bool UnmarkPending(int id)
        {
            byte _;
            if (pendingPortraitPawnIds.TryRemove(id, out _))
            {
                System.Threading.Interlocked.Decrement(ref pendingCount);
                return true;
            }
            return false;
        }
        public static bool IsPending(int id) => pendingPortraitPawnIds.ContainsKey(id);
        public static int PendingCount => pendingCount;

        public static bool MarkAutoGen(int id) => autoGenTriggered.TryAdd(id, 0);
        public static bool UnmarkAutoGen(int id) { byte _; return autoGenTriggered.TryRemove(id, out _); }
        public static bool IsAutoGenMarked(int id) => autoGenTriggered.ContainsKey(id);

        // RecordFailedAttempt increments the per-pawn failure counter and
        // returns the NEW count. Callers compare against MaxRetryAttempts to
        // decide whether to give up on the pawn entirely.
        public static int RecordFailedAttempt(int id) =>
            failedAttempts.AddOrUpdate(id, 1, (k, v) => v + 1);
        public static int GetFailedAttempts(int id)
        {
            int v;
            return failedAttempts.TryGetValue(id, out v) ? v : 0;
        }
        public static void ClearFailedAttempts(int id) { int _; failedAttempts.TryRemove(id, out _); }
        public static int FailedPermanentlyCount
        {
            get
            {
                int n = 0;
                foreach (var kv in failedAttempts) if (kv.Value >= MaxRetryAttempts) n++;
                return n;
            }
        }
        public static void ResetAllFailedPawns()
        {
            // Lift each permanently-failed pawn out of all three tracking
            // sets so the next safety scan re-enqueues them from scratch.
            List<int> ids = new List<int>();
            foreach (var kv in failedAttempts) if (kv.Value >= MaxRetryAttempts) ids.Add(kv.Key);
            foreach (int id in ids)
            {
                ClearFailedAttempts(id);
                UnmarkAutoGen(id);
                UnmarkPending(id);
            }
        }

        private Vector2 scrollPosition = Vector2.zero;
        // each manager stores a pawn, if any, and the avatar texture

        [DebugAction("Avatar", "Reload Textures")]
        public static void ClearCachedTextures()
        {
            foreach (KeyValuePair<string, Texture2D> kvp in cachedTextures)
                UnityEngine.Object.Destroy(kvp.Value);
            cachedTextures.Clear();
            ClearCachedAvatars();
        }

        public static void ClearCachedAvatars()
        {
            mainManager.ClearCachedAvatar();
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in colonistBarManagers)
                kvp.Value.ClearCachedAvatar();
            colonistBarManagers.Clear();
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in questTabManagers)
                kvp.Value.ClearCachedAvatar();
            questTabManagers.Clear();
        }

        // Review #3: cached AvatarManagers used to leak forever — every raider
        // / visitor / trader pinned a Pawn reference and held a Texture2D
        // canvas. Called every 20s from AutoPortraitGenerator's safety scan.
        // Eviction criteria: the pawn is null, destroyed, or discarded — i.e.
        // there's no in-game reason to keep rendering them. A still-alive
        // off-map pawn (caravan / quest holding pen) stays cached so re-opening
        // the inspect pane on them doesn't re-render from scratch.
        public static void SweepDeadPawnManagers()
        {
            int colonistEvicted = SweepOneDict(colonistBarManagers);
            int questEvicted = SweepOneDict(questTabManagers);
            if (colonistEvicted + questEvicted > 0)
            {
                Log.Message("Avatar: swept " + (colonistEvicted + questEvicted)
                    + " dead-pawn avatar manager(s) (" + colonistEvicted + " colonist-bar, "
                    + questEvicted + " quest-tab).");
            }
        }
        private static int SweepOneDict(Dictionary<Pawn, AvatarManager> dict)
        {
            List<Pawn> dead = null;
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in dict)
            {
                Pawn p = kvp.Key;
                if (p == null || p.Destroyed || p.Discarded)
                {
                    if (dead == null) dead = new List<Pawn>();
                    dead.Add(p);
                }
            }
            if (dead == null) return 0;
            foreach (Pawn p in dead)
            {
                try { dict[p].ClearCachedAvatar(); } catch { }
                dict.Remove(p);
            }
            return dead.Count;
        }

        public Texture2D GetTexture(string texPath, bool fallback=true)
        {
            if (string.IsNullOrEmpty(texPath)) return null;
            if (!cachedTextures.ContainsKey(texPath))
            {
                string path = Content.RootDir+"/Assets/"+texPath+".png";
                if (!System.IO.File.Exists(path))
                { // fallback to RW texture manager
                    return fallback ? ContentFinder<Texture2D>.Get(texPath) : null;
                }
                Texture2D newTexture = new (1, 1);
                newTexture.LoadImage(System.IO.File.ReadAllBytes(path));
                cachedTextures[texPath] = newTexture;
            }
            return cachedTextures[texPath];
        }

        public AvatarMod(ModContentPack content) : base(content)
        {
            AvatarManager.mod = this;
            settings = GetSettings<AvatarSettings>();
        }

        public override string SettingsCategory() => "Avatar - Personas";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = inRect;
            Rect contentRect = new Rect(0f, 0f, viewRect.width - 16f, 7000f);

            Widgets.BeginScrollView(viewRect, ref scrollPosition, contentRect);

            Listing_Standard listingStandard = new Listing_Standard()
            {
                ColumnWidth = contentRect.width - 40,
            };
            listingStandard.Begin(contentRect);
            #if v1_3
            listingStandard.Label("Avatar size");
            settings.avatarWidth = (float)Math.Round(
                listingStandard.Slider(settings.avatarWidth, 80f, 400f));
            #else
            settings.avatarWidth = (float)Math.Round(
                listingStandard.SliderLabeled("Avatar size", settings.avatarWidth, 80f, 400f));
            #endif
            string samplePath = "UI/AvatarSampleS";
            Texture2D avatar = GetTexture(samplePath);
            avatar.filterMode = FilterMode.Point;
            float width = settings.avatarWidth;
            float height = width*avatar.height/avatar.width;
            listingStandard.ButtonImage(avatar, width, height);

            // ============================================================
            // BEHAVIOUR CHECKBOXES
            // ============================================================
            listingStandard.CheckboxLabeled("Hide main avatar", ref settings.hideMainAvatar);
            listingStandard.CheckboxLabeled("Show avatars in colonist bar", ref settings.showInColonistBar);
            if (settings.showInColonistBar && !ModCompatibility.ColonyGroups_Loaded)
            {
                #if v1_3
                listingStandard.Label("Colonist bar size adjustment");
                settings.showInColonistBarSizeAdjust = (float)(
                    listingStandard.Slider(settings.showInColonistBarSizeAdjust, 0f, 10f));
                #else
                settings.showInColonistBarSizeAdjust = (float)(
                    listingStandard.SliderLabeled("Colonist bar size adjustment", settings.showInColonistBarSizeAdjust, 0f, 10f));
                #endif
            }
            if (ModCompatibility.CCMBar_Loaded && listingStandard.ButtonText("Refresh colonist bar"))
            {
                AccessTools.Method("ColoredMoodBar13.MoodPatch:CGMarkColonistsDirty").Invoke(null, new object[] {null});
            }
            listingStandard.CheckboxLabeled("Show avatars in quest tab (experimental)", ref settings.showInQuestTab);
            listingStandard.CheckboxLabeled("Auto-generate AI portraits at load/spawn (uses API credits!)", ref settings.autoGeneratePortraits);
            listingStandard.GapLine();
            // ============================================================
            // API PROVIDER
            // ============================================================
            Text.Font = GameFont.Medium;
            listingStandard.Label((TaggedString)("API Provider: " + GetProviderLabel(settings.apiProvider)));
            Text.Font = GameFont.Small;
            listingStandard.Label((TaggedString)"Select the AI service used for portrait generation:", -1);
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.GoogleGemini ? "> " : "   ") + "Google Gemini (free tier, recommended)"))
                settings.apiProvider = ApiProvider.GoogleGemini;
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.NagaAc ? "> " : "   ") + "Naga.ac (free tier, Flux/SDXL/DALL-E)"))
                settings.apiProvider = ApiProvider.NagaAc;
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.Pixazo ? "> " : "   ") + "Pixazo (free tier, SDXL)"))
                settings.apiProvider = ApiProvider.Pixazo;
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.StabilityAI ? "> " : "   ") + "StabilityAI (paid, img2img)"))
                settings.apiProvider = ApiProvider.StabilityAI;
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.OpenRouter ? "> " : "   ") + "OpenRouter (paid, multi-model)"))
                settings.apiProvider = ApiProvider.OpenRouter;
            if (listingStandard.ButtonText((settings.apiProvider == ApiProvider.Generic ? "> " : "   ") + "Generic / Custom API"))
                settings.apiProvider = ApiProvider.Generic;
            listingStandard.GapLine();
            // Show fields for the selected provider
            switch (settings.apiProvider)
            {
                case ApiProvider.GoogleGemini:
                    listingStandard.Label((TaggedString)"Gemini API Key:", -1);
                    settings.geminiApiKey = listingStandard.TextEntry(settings.geminiApiKey);
                    listingStandard.Label((TaggedString)"Gemini Model:", -1);
                    settings.geminiModel = listingStandard.TextEntry(settings.geminiModel);
                    break;
                case ApiProvider.NagaAc:
                    listingStandard.Label((TaggedString)"Naga.ac API Key:", -1);
                    settings.nagaAcApiKey = listingStandard.TextEntry(settings.nagaAcApiKey);
                    listingStandard.Label((TaggedString)"Naga.ac Model:", -1);
                    settings.nagaAcModel = listingStandard.TextEntry(settings.nagaAcModel);
                    break;
                case ApiProvider.Pixazo:
                    listingStandard.Label((TaggedString)"Pixazo API Key:", -1);
                    settings.pixazoApiKey = listingStandard.TextEntry(settings.pixazoApiKey);
                    listingStandard.Label((TaggedString)"Pixazo Model:", -1);
                    settings.pixazoModel = listingStandard.TextEntry(settings.pixazoModel);
                    break;
                case ApiProvider.StabilityAI:
                    listingStandard.Label((TaggedString)"StabilityAI API Key:", -1);
                    settings.stabilityApiKey = listingStandard.TextEntry(settings.stabilityApiKey);
                    listingStandard.Label((TaggedString)"StabilityAI Endpoint:", -1);
                    settings.stabilityEndpoint = listingStandard.TextEntry(settings.stabilityEndpoint);
                    break;
                case ApiProvider.OpenRouter:
                    listingStandard.Label((TaggedString)"OpenRouter API Key:", -1);
                    settings.openRouterApiKey = listingStandard.TextEntry(settings.openRouterApiKey);
                    listingStandard.Label((TaggedString)"OpenRouter Model:", -1);
                    settings.openRouterModel = listingStandard.TextEntry(settings.openRouterModel);
                    break;
                case ApiProvider.Generic:
                    listingStandard.Label((TaggedString)"Generic API Key:", -1);
                    settings.genericApiKey = listingStandard.TextEntry(settings.genericApiKey);
                    listingStandard.Label((TaggedString)"Generic Endpoint URL:", -1);
                    settings.genericEndpoint = listingStandard.TextEntry(settings.genericEndpoint);
                    listingStandard.Label((TaggedString)"Generic Model:", -1);
                    settings.genericModel = listingStandard.TextEntry(settings.genericModel);
                    break;
            }
            listingStandard.GapLine();
            // ============================================================
            // ART STYLE SELECTOR
            // ============================================================
            Text.Font = GameFont.Medium;
            listingStandard.Label((TaggedString)("Art Style: " + ArtStylePrompts.GetLabel(settings.artStyle)));
            Text.Font = GameFont.Small;
            listingStandard.Label((TaggedString)"Select the visual style applied to all AI-generated portraits:", -1);
            foreach (ArtStyle style in Enum.GetValues(typeof(ArtStyle)))
            {
                string label = ArtStylePrompts.GetLabel(style);
                bool selected = settings.artStyle == style;
                if (listingStandard.ButtonText((selected ? "> " : "   ") + label))
                {
                    settings.artStyle = style;
                }
            }
            if (settings.artStyle == ArtStyle.Custom)
            {
                listingStandard.Label((TaggedString)"Custom style prompt:", -1);
                settings.customStylePrompt = listingStandard.TextEntry(settings.customStylePrompt);
            }
            listingStandard.GapLine();

            // ============================================================
            // BASE PROMPTS
            // ============================================================
            listingStandard.Label((TaggedString)"Humanoid prompt template", -1, "Placeholders: {age}, {gender}, {race}, {lifestage}, {bodytype}, {skincolor}, {haircolor}, {hair}, {beard}, {apparel}, {items}, {mood}, {personality}, {traits}, {health}, {implants}, {prosthetics}.");
            settings.aiGenPreamble = listingStandard.TextEntry(settings.aiGenPreamble);
            if (listingStandard.ButtonText("Reset humanoid template")) settings.aiGenPreamble = settings.aiGenPreambleDefault;
            listingStandard.Label((TaggedString)"Humanoid negative prompt:", -1);
            settings.apiNegativePrompt = listingStandard.TextEntry(settings.apiNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Animal template (mammals, birds)", -1, "Placeholders: {size}, {age}, {gender}, {race}, {lifestage}, {description}, {health}.");
            settings.aiGenAnimalPreamble = listingStandard.TextEntry(settings.aiGenAnimalPreamble);
            if (listingStandard.ButtonText("Reset animal template")) settings.aiGenAnimalPreamble = settings.aiGenAnimalPreambleDefault;
            listingStandard.Label((TaggedString)"Animal negative prompt:", -1);
            settings.animalNegativePrompt = listingStandard.TextEntry(settings.animalNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Insect / Reptile template", -1, "Placeholders: {race}, {size}, {lifestage}, {description}, {health}.");
            settings.aiGenInsectPreamble = listingStandard.TextEntry(settings.aiGenInsectPreamble);
            if (listingStandard.ButtonText("Reset insect template")) settings.aiGenInsectPreamble = settings.aiGenInsectPreambleDefault;
            listingStandard.Label((TaggedString)"Insect negative prompt:", -1);
            settings.insectNegativePrompt = listingStandard.TextEntry(settings.insectNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Dragon template", -1, "Placeholders: {size}, {race}, {lifestage}, {description}, {health}.");
            settings.aiGenDragonPreamble = listingStandard.TextEntry(settings.aiGenDragonPreamble);
            if (listingStandard.ButtonText("Reset dragon template")) settings.aiGenDragonPreamble = settings.aiGenDragonPreambleDefault;
            listingStandard.Label((TaggedString)"Dragon negative prompt:", -1);
            settings.dragonNegativePrompt = listingStandard.TextEntry(settings.dragonNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Aquatic template", -1, "Placeholders: {race}, {size}, {lifestage}, {description}, {health}.");
            settings.aiGenAquaticPreamble = listingStandard.TextEntry(settings.aiGenAquaticPreamble);
            if (listingStandard.ButtonText("Reset aquatic template")) settings.aiGenAquaticPreamble = settings.aiGenAquaticPreambleDefault;
            listingStandard.Label((TaggedString)"Aquatic negative prompt:", -1);
            settings.aquaticNegativePrompt = listingStandard.TextEntry(settings.aquaticNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Plant / Dryad template", -1, "Placeholders: {race}, {size}, {lifestage}, {description}, {health}.");
            settings.aiGenPlantPreamble = listingStandard.TextEntry(settings.aiGenPlantPreamble);
            if (listingStandard.ButtonText("Reset plant template")) settings.aiGenPlantPreamble = settings.aiGenPlantPreambleDefault;
            listingStandard.Label((TaggedString)"Plant negative prompt:", -1);
            settings.plantNegativePrompt = listingStandard.TextEntry(settings.plantNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Mechanoid template", -1, "Placeholders: {race}, {size}, {description}, {health}.");
            settings.aiGenMechPreamble = listingStandard.TextEntry(settings.aiGenMechPreamble);
            if (listingStandard.ButtonText("Reset mech template")) settings.aiGenMechPreamble = settings.aiGenMechPreambleDefault;
            listingStandard.Label((TaggedString)"Mechanoid negative prompt:", -1);
            settings.mechNegativePrompt = listingStandard.TextEntry(settings.mechNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Entity template (undead, demons, elementals, etc.)", -1, "Placeholders: {race}, {size}, {description}, {health}.");
            settings.aiGenEntityPreamble = listingStandard.TextEntry(settings.aiGenEntityPreamble);
            if (listingStandard.ButtonText("Reset entity template")) settings.aiGenEntityPreamble = settings.aiGenEntityPreambleDefault;
            listingStandard.Label((TaggedString)"Entity negative prompt:", -1);
            settings.entityNegativePrompt = listingStandard.TextEntry(settings.entityNegativePrompt);
            listingStandard.GapLine();
            
            listingStandard.Label((TaggedString)"Other / Unknown template (fallback)", -1, "Placeholders: {race}, {description}, {health}.");
            settings.aiGenOtherPreamble = listingStandard.TextEntry(settings.aiGenOtherPreamble);
            if (listingStandard.ButtonText("Reset other template")) settings.aiGenOtherPreamble = settings.aiGenOtherPreambleDefault;
            listingStandard.Label((TaggedString)"Other negative prompt:", -1);
            settings.otherNegativePrompt = listingStandard.TextEntry(settings.otherNegativePrompt);
            listingStandard.GapLine();
            // ============================================================
            // END BASE PROMPTS
            // ============================================================

            // ============================================================
            // UTILITY BUTTONS
            // ============================================================
            // Re-enqueue all player-faction pawns without portraits on disk.
            // Bypasses the retry-budget cap and autoGenTriggered cache.
            if (listingStandard.ButtonText("Regenerate missing portraits for all colonists"))
            {
                int n = AutoPortraitGenerator.RegenerateMissingForAllColonists();
                Messages.Message(
                    n == 0
                        ? "All colonists already have portraits — nothing to enqueue."
                        : ("Enqueued " + n + " missing portrait" + (n == 1 ? "" : "s") + ". The queue will pick them up over the next few seconds."),
                    n == 0 ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.TaskCompletion,
                    historical: false);
            }
            int permaFailed = AvatarMod.FailedPermanentlyCount;
            if (permaFailed > 0 && listingStandard.ButtonText("Reset " + permaFailed + " failed pawn" + (permaFailed == 1 ? "" : "s") + " (retry from scratch)"))
            {
                AvatarMod.ResetAllFailedPawns();
                Messages.Message("Reset " + permaFailed + " failed pawn(s). The periodic scan will re-enqueue them within 20 seconds.", MessageTypeDefOf.TaskCompletion, historical: false);
            }
            listingStandard.GapLine();
            if (listingStandard.ButtonText("Open portraits folder"))
            {
                AIGen.OpenAvatarFolder();
            }
            listingStandard.End();
            Widgets.EndScrollView();
            base.DoSettingsWindowContents(inRect);
        }

        public Texture2D GetColonistBarAvatar(Pawn pawn, bool drawHeadgear, bool drawClothes)
        {
            if (!colonistBarManagers.TryGetValue(pawn, out AvatarManager manager))
            {
                manager = new ();
                manager.SetPawn(pawn);
                manager.SetBGColor(new Color(0,0,0,0));
                manager.SetCheckDowned(true);
                colonistBarManagers[pawn] = manager;
            }
            manager.drawHeadgear = drawHeadgear;
            manager.drawClothes = drawClothes;
            return manager.GetAvatar();
        }

        public Texture2D GetQuestTabAvatar(Pawn pawn)
        {
            if (!questTabManagers.ContainsKey(pawn))
            {
                AvatarManager manager = new ();
                manager.SetPawn(pawn);
                questTabManagers[pawn] = manager;
            }
            return questTabManagers[pawn].GetAvatar();
        }

        private static string GetProviderLabel(ApiProvider p)
        {
            switch (p)
            {
                case ApiProvider.GoogleGemini: return "Google Gemini";
                case ApiProvider.NagaAc:      return "Naga.ac";
                case ApiProvider.Pixazo:      return "Pixazo";
                case ApiProvider.StabilityAI: return "StabilityAI";
                case ApiProvider.OpenRouter:  return "OpenRouter";
                case ApiProvider.Generic:     return "Generic / Custom";
                default:                      return "Unknown";
            }
        }

        public static string GetArtStylePrompt(ArtStyle style, string customPrompt)
        {
            return ArtStylePrompts.GetPrompt(style, customPrompt);
        }

        public static string GetFullNegativePrompt(AvatarSettings s)
        {
            if (!string.IsNullOrEmpty(s.apiNegativePrompt))
                return s.apiNegativePrompt;
            
            string baseNeg = "background, scenery, landscape orientation, horizontal composition, panoramic, wide aspect ratio, nature, outdoor, indoor, room, wall, multiple people, group, crowd, extra characters, extra person, full body, full shot, wide shot, three-quarter shot, distant, far away, side profile, profile view, back view, looking away, turned head, low quality, low res, worst quality, jpeg artifacts, bad quality, blurry, out of focus, motion blur, distorted, warped, ugly, deformed, missing face, cropped face, cut off, bad framing, off-center, watermark, text, signature, logo, website, username, 3d model, cgi, plastic, doll, wax figure, mannequin, duplicate, cloned, mirrored, extra limbs, extra fingers, mutated, fused";
            
            string artNeg = ArtStylePrompts.GetNegativePrompt(s.artStyle);
            if (string.IsNullOrEmpty(artNeg)) return baseNeg;
            return baseNeg + ", " + artNeg;
        }

        public static string GetFullCreatureNegativePrompt(AvatarSettings s)
        {
            // Use the generic creature default (no category-specific override here — 
            // the per-category overrides are used by Prompts_Window at generation time)
            string baseNeg = "background, scenery, landscape orientation, horizontal composition, panoramic, nature, outdoor, indoor, room, wall, multiple creatures, group, herd, flock, extra animals, full body, full shot, wide shot, three-quarter shot, distant, far away, side profile, profile view, back view, looking away, turned head, low quality, low res, worst quality, jpeg artifacts, bad quality, blurry, out of focus, motion blur, distorted, warped, ugly, deformed, cut off, bad framing, off-center, watermark, text, signature, logo, website, username, 3d model, cgi, plastic, toy, duplicate, cloned, mirrored, extra legs, extra tails, mutated, fused, human, human face, human hands, anthropomorphic, wrong species, hybrid, chimeric";
            
            string artNeg = ArtStylePrompts.GetNegativePrompt(s.artStyle);
            if (string.IsNullOrEmpty(artNeg)) return baseNeg;
            return baseNeg + ", " + artNeg;
        }
    }

    [HarmonyPatch(typeof(InspectPaneUtility), nameof(InspectPaneUtility.DoTabs))]
    public static class UIPatch
    {
        static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();

        // ============================================================
        // On-demand AI generation: right-click → "Generate portrait".
        // No automatic enqueue on selection — pixel-art avatar renders
        // immediately; the player explicitly requests AI generation.
        // ============================================================
        private static int lastSeenPawnId = -1;

        private static Vector2 relPos(Vector2 absPos, Rect rect)
        {
            return new((absPos.x-rect.x)/rect.width, 1f-(absPos.y-rect.y)/rect.height);
        }
        public static void Postfix(IInspectPane pane)
        {
            if (pane is not MainTabWindow_Inspect inspectPanel) return;
            Pawn pawn = null;
            if (inspectPanel.SelThing is Pawn selectedPawn)
                pawn = selectedPawn;
            else if (inspectPanel.SelThing is Corpse corpse)
                pawn = corpse.InnerPawn;
            if (pawn != null)
            {
                AvatarManager manager = AvatarMod.mainManager;
                manager.SetPawn(pawn);
                manager.drawClothes = !ModCompatibility.ModdedNudity(pawn);

                // Use the new rename-stable path (<thingIDNumber>.png). Lazy
                // legacy-file migration is handled inside GetPortraitPath, so
                // a colonist who got their portrait under the old <name>_<id>
                // naming will still resolve to the same image.
                string portraitPath = AvatarManager.GetPortraitPath(pawn);

                // Track which pawn the inspect pane is currently showing.
                // Updates lastSeenPawnId so a re-selection of the same pawn
                // after viewing someone else still passes the per-streak guard.
                if (pawn.thingIDNumber != lastSeenPawnId)
                {
                    lastSeenPawnId = pawn.thingIDNumber;
                }

                // AI generation is fully on-demand. The player must explicitly
                // right-click → "Generate portrait" to trigger AI generation.
                // Pixel-art avatar is shown automatically on selection.

                if (!mod.settings.hideMainAvatar && inspectPanel.OpenTabType is null)
                {
                    bool isHidden = AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber);
                    Texture2D avatar = manager.GetAvatar();
                    float width = mod.settings.avatarWidth;
                    float height = width*avatar.height/avatar.width;
                    float left = 0f;
                    if (ModCompatibility.Portraits_Loaded && pawn.ageTracker.AgeBiologicalYearsFloat >= 7 && !pawn.Dead)
                    {
                        // move avatar to the right of the portrait
                        left = 30f;
                        if (ModCompatibility.PortraitShown(pawn)) left += 185f;
                    }
                    Rect rect = new(left, inspectPanel.PaneTopY - InspectPaneUtility.TabHeight - height, width, height);
                    // Always draw the avatar when not user-hidden. The manager
                    // returns the AI static portrait when one exists, else the
                    // pixel-art render. AI generation is fully on-demand via
                    // right-click → "Generate portrait".
                    if (!isHidden)
                    {
                        GUI.DrawTexture(rect, avatar);
                    }
                    // Bottom-left mini-spinner during portrait generation.
                    // Same 8-dot animation as the colonist-bar overlay, but:
                    //  - no scrim (don't tint the portrait)
                    //  - small fixed size (12px radius, 3px dots) regardless of
                    //    settings.avatarWidth so it stays unobtrusive on large
                    //    avatar settings
                    //  - anchored to the bottom-left with 8px inset padding
                    // Gated on per-pawn pending state AND the same isHidden
                    // guard as the avatar itself — if the user has hidden this
                    // pawn's avatar, don't draw spinner feedback either.
                    if (!isHidden && AvatarMod.IsPending(pawn.thingIDNumber))
                    {
                        const float spinnerRadius = 10f;
                        const float spinnerDotSize = 3f;
                        const float spinnerInset = 8f;
                        Color savedColor = GUI.color;
                        try
                        {
                            Vector2 spinnerCenter = new Vector2(
                                rect.xMin + spinnerInset + spinnerRadius,
                                rect.yMax - spinnerInset - spinnerRadius);
                            ColonistBar_SpinnerOverlay_Patch.DrawDotSpinner(
                                spinnerCenter, spinnerRadius, spinnerDotSize);
                        }
                        finally { GUI.color = savedColor; }
                    }
                    if (Event.current.type == EventType.MouseDown && Mouse.IsOver(rect)
                        && (isHidden || manager.CheckCursor(relPos(Event.current.mousePosition, rect))))
                    { // capture mouse click
                        if (Event.current.button == 0 && !isHidden) // leftbutton
                            manager.drawHeadgear = !manager.drawHeadgear;
                        else if (Event.current.button == 1) // rightbutton
                            Find.WindowStack.Add(manager.GetFloatMenu());
                        Event.current.Use();
                    }
                }
            }
        }
    }

    // basically borrrowed from Portraits of the Rim
    [HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoFactionInfo))]
    public static class QuestWindowPatch
    {
        static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        public static void Prefix(ref MainTabWindow_Quests __instance, Rect rect, ref float curY)
        {
            if (mod.settings.showInQuestTab)
            {
                List<Pawn> pawns = new();
                foreach (var part in __instance.selected.PartsListForReading)
                {
                    List<Pawn> partPawns;
                    if (part is QuestPart_PawnsArrive pawnsArrive)
                        partPawns = pawnsArrive.pawns?.ToList();
                    else if (part is QuestPart_ExtraFaction extraFaction)
                        partPawns = extraFaction.affectedPawns?.ToList();
                    #if v1_3 || v1_4 || v1_5
                    else if (part is QuestPart_Hyperlinks hyperlinks)
                        partPawns = hyperlinks.pawns?.ToList();
                    #endif
                    else
                        partPawns = part.QuestLookTargets.Where(x => x.Thing is Pawn).Select(x => x.Thing).Cast<Pawn>().ToList();
                    foreach (Pawn pawn in partPawns)
                    {
                        if (!pawns.Contains(pawn))
                            pawns.Add(pawn);
                    }
                }
                if (pawns.Count > 0)
                {
                    float width = pawns.Count > 4 ? 80f : 120f;
                    float height = width*1.2f;
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Rect avatarRect = new(rect.width - (width+5)*(i%5+1), curY+15+(height+5)*(i/5), width, height);
                        GUI.DrawTexture(avatarRect, mod.GetQuestTabAvatar(pawns[i]));
                        if (Mouse.IsOver(avatarRect))
                        {
                            TooltipHandler.TipRegion(avatarRect, pawns[i].LabelCap);
                        }
                    }
                    curY += 10f+(height+5f)*(float)Math.Ceiling(pawns.Count/5f);
                }
            }
        }
    }

    // disable some of vanilla's portrait updating
    [HarmonyPatch(typeof(Verse.AI.JobDriver), nameof(Verse.AI.JobDriver.SetInitialPosture))]
    public static class AvatarJobDriverPatch
    {
        private static MethodInfo oldMethod = AccessTools.Method(typeof(PortraitsCache), "SetDirty");
        private static MethodInfo newMethod = AccessTools.Method("AvatarJobDriverPatch:SetDirty");
        public static void SetDirty(Pawn _)
        {
            // DO NOTHING!
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
                yield return (instruction.Calls(oldMethod)) ? new CodeInstruction(OpCodes.Call, newMethod) : instruction;
        }
    }

    // Heal function calls unnecessary updates, which becomes a problem for entities with regeneration
    // Removing them might not be the best solution, but what the hell
    [HarmonyPatch(typeof(Verse.Hediff_Injury), nameof(Verse.Hediff_Injury.Heal))]
    public static class AvatarHealPatch
    {
        private static MethodInfo oldMethod = AccessTools.Method(typeof(Verse.Pawn_HealthTracker), "Notify_HediffChanged");
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
                if (instruction.Calls(oldMethod))
                {
                    // clear the stack then do a nop
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Nop);
                }
                else
                    yield return instruction;
        }
    }

    // redraw avatar whenever ingame portrait got redrawn
    [HarmonyPatch(typeof(PortraitsCache), nameof(PortraitsCache.SetDirty))]
    public static class AvatarUpdateHookPatch
    {
        public static void Postfix(Pawn pawn)
        {
            if (pawn == AvatarMod.mainManager.pawn)
                AvatarMod.mainManager.ClearCachedAvatar();
            if (AvatarMod.colonistBarManagers.ContainsKey(pawn))
                AvatarMod.colonistBarManagers[pawn].ClearCachedAvatar();
            if (AvatarMod.questTabManagers.ContainsKey(pawn))
                AvatarMod.questTabManagers[pawn].ClearCachedAvatar();
        }
    }

    // redraw avatar when pawn ages (for wrinkles)
    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.BirthdayBiological))]
    public static class Pawn_AgeTracker_BirthdayBiological_Patch
    {
        public static void Postfix(ref Pawn_AgeTracker __instance)
        {
            if (__instance.pawn == AvatarMod.mainManager.pawn)
                AvatarMod.mainManager.ClearCachedAvatar();
            if (AvatarMod.colonistBarManagers.ContainsKey(__instance.pawn))
                AvatarMod.colonistBarManagers[__instance.pawn].ClearCachedAvatar();
            if (AvatarMod.questTabManagers.ContainsKey(__instance.pawn))
                AvatarMod.questTabManagers[__instance.pawn].ClearCachedAvatar();
        }
    }

    public class AvatarSettings : ModSettings
    {
        public float avatarWidth = 200f;
        // The next 9 fields are still consumed by RenderAvatar / Prompts_Window
        // at runtime, but are no longer exposed in the settings UI. Their
        // Scribe_Values.Look calls were removed so the user's settings XML
        // stops accumulating dead keys (see review #11). Defaults are now the
        // only values these ever take.
        public bool avatarCompression = false;
        public bool avatarScaling = true;
        public bool defaultDrawHeadgear = true;
        public bool showHairWithHeadgear = true;
        public bool hideMainAvatar = false;
        public bool showInQuestTab = true;
        public bool showInColonistBar = true;
        public float showInColonistBarSizeAdjust = 0f;
        public bool noFemaleLips = false;
        public bool noWrinkles = false;
        public bool earsOnTop = true;
        public bool noCorpseGore = false;
        public bool autoGeneratePortraits = false;
        public string aiGenPreamble = "A front-facing portrait of a {age}-year-old {gender} {race}, {lifestage}, {bodytype} build, {skincolor} skin, {haircolor} hair. {hair}. {beard}. Wearing {apparel}. {items}. {mood}. {personality}. Traits: {traits}. Health: {health}. {implants}. {prosthetics}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenPreambleDefault = "A front-facing portrait of a {age}-year-old {gender} {race}, {lifestage}, {bodytype} build, {skincolor} skin, {haircolor} hair. {hair}. {beard}. Wearing {apparel}. {items}. {mood}. {personality}. Traits: {traits}. Health: {health}. {implants}. {prosthetics}. isolated on solid white background, studio lighting, plain backdrop.";
        public float aiGenVanillaPortraitOffset = 0.5f;

        // API provider settings
        public ApiProvider apiProvider = ApiProvider.GoogleGemini;
        public float apiCfgScale = 7f;
        public int apiSteps = 30;
        public string apiSampler = "";
        public string apiScheduler = "";
        public string apiStylePreset = "";
        public bool apiPrependPositive = false;
        public string apiPositiveStylePrompt = "";

        // Google Gemini
        public string geminiApiKey = "";
        public string geminiModel = "gemini-3.1-flash-lite-image";

        // OpenRouter
        public string openRouterApiKey = "";
        public string openRouterModel = "";

        // Pixazo
        public string pixazoApiKey = "";
        public string pixazoModel = "sdxl-base";

        // Naga.ac (OpenAI-compatible, free tier)
        public string nagaAcApiKey = "";
        public string nagaAcModel = "flux-1-schnell:free";

        // Stability AI
        public string stabilityApiKey = "";
        public string stabilityEndpoint = "";

        // Generic
        public string genericApiKey = "";
        public string genericEndpoint = "";
        public string genericModel = "";
        public string genericRequestTemplate = "";
        public string genericResponseImagePath = "";

        // Art style
        public ArtStyle artStyle = ArtStyle.None;
        public string customStylePrompt = "";

        // Negative prompt overrides (empty = use hardcoded default)
        public string apiNegativePrompt = "";
        public string animalNegativePrompt = "";
        public string insectNegativePrompt = "";
        public string dragonNegativePrompt = "";
        public string aquaticNegativePrompt = "";
        public string plantNegativePrompt = "";
        public string mechNegativePrompt = "";
        public string entityNegativePrompt = "";
        public string otherNegativePrompt = "";

        // Creature positive templates — one per category
        public string aiGenAnimalPreamble = "A front-facing portrait of a {size} {age}-year-old {gender} {race}, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenAnimalPreambleDefault = "A front-facing portrait of a {size} {age}-year-old {gender} {race}, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenInsectPreamble = "A front-facing close-up of a {race}, {size} size, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenInsectPreambleDefault = "A front-facing close-up of a {race}, {size} size, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenDragonPreamble = "A majestic front-facing portrait of a {size} {race}, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, epic fantasy.";
        public string aiGenDragonPreambleDefault = "A majestic front-facing portrait of a {size} {race}, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, epic fantasy.";
        public string aiGenAquaticPreamble = "A front-facing portrait of a {race}, {size} aquatic creature, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenAquaticPreambleDefault = "A front-facing portrait of a {race}, {size} aquatic creature, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenPlantPreamble = "A front-facing portrait of a {race}, {size} plant creature, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, botanical.";
        public string aiGenPlantPreambleDefault = "A front-facing portrait of a {race}, {size} plant creature, {lifestage}. {description}. {health}. isolated on solid white background, studio lighting, botanical.";
        public string aiGenMechPreamble = "A front-facing portrait of a {race}, a {size} mechanical unit. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenMechPreambleDefault = "A front-facing portrait of a {race}, a {size} mechanical unit. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenEntityPreamble = "A front-facing portrait of a {race}, a {size} supernatural entity. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenEntityPreambleDefault = "A front-facing portrait of a {race}, a {size} supernatural entity. {description}. {health}. isolated on solid white background, studio lighting.";
        public string aiGenOtherPreamble = "A front-facing portrait of a {race}. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";
        public string aiGenOtherPreambleDefault = "A front-facing portrait of a {race}. {description}. {health}. isolated on solid white background, studio lighting, plain backdrop.";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref avatarWidth, "avatarWidth");
            Scribe_Values.Look(ref hideMainAvatar, "hideMainAvatar");
            Scribe_Values.Look(ref showInQuestTab, "showInQuestTab");
            Scribe_Values.Look(ref showInColonistBar, "showInColonistBar");
            Scribe_Values.Look(ref showInColonistBarSizeAdjust, "showInColonistBarSizeAdjust");
            Scribe_Values.Look(ref autoGeneratePortraits, "autoGeneratePortraits", false);
            Scribe_Values.Look(ref aiGenPreamble, "aiGenPreamble");
            Scribe_Values.Look(ref apiProvider, "apiProvider", ApiProvider.GoogleGemini);
            Scribe_Values.Look(ref apiCfgScale, "apiCfgScale", 7f);
            Scribe_Values.Look(ref apiSteps, "apiSteps", 30);
            Scribe_Values.Look(ref apiSampler, "apiSampler");
            Scribe_Values.Look(ref apiScheduler, "apiScheduler");
            Scribe_Values.Look(ref apiStylePreset, "apiStylePreset");
            Scribe_Values.Look(ref apiPrependPositive, "apiPrependPositive");
            Scribe_Values.Look(ref apiPositiveStylePrompt, "apiPositiveStylePrompt");
            Scribe_Values.Look(ref geminiApiKey, "geminiApiKey");
            Scribe_Values.Look(ref geminiModel, "geminiModel", "gemini-3.1-flash-lite-image");
            Scribe_Values.Look(ref openRouterApiKey, "openRouterApiKey");
            Scribe_Values.Look(ref openRouterModel, "openRouterModel");
            Scribe_Values.Look(ref pixazoApiKey, "pixazoApiKey");
            Scribe_Values.Look(ref pixazoModel, "pixazoModel", "sdxl-base");
            Scribe_Values.Look(ref nagaAcApiKey, "nagaAcApiKey");
            Scribe_Values.Look(ref nagaAcModel, "nagaAcModel", "flux-1-schnell:free");
            Scribe_Values.Look(ref stabilityApiKey, "stabilityApiKey");
            Scribe_Values.Look(ref stabilityEndpoint, "stabilityEndpoint");
            Scribe_Values.Look(ref genericApiKey, "genericApiKey");
            Scribe_Values.Look(ref genericEndpoint, "genericEndpoint");
            Scribe_Values.Look(ref genericModel, "genericModel");
            Scribe_Values.Look(ref genericRequestTemplate, "genericRequestTemplate");
            Scribe_Values.Look(ref genericResponseImagePath, "genericResponseImagePath");
            Scribe_Values.Look(ref artStyle, "artStyle", ArtStyle.None);
            Scribe_Values.Look(ref customStylePrompt, "customStylePrompt");
            Scribe_Values.Look(ref apiNegativePrompt, "apiNegativePrompt");
            Scribe_Values.Look(ref animalNegativePrompt, "animalNegativePrompt");
            Scribe_Values.Look(ref insectNegativePrompt, "insectNegativePrompt");
            Scribe_Values.Look(ref dragonNegativePrompt, "dragonNegativePrompt");
            Scribe_Values.Look(ref aquaticNegativePrompt, "aquaticNegativePrompt");
            Scribe_Values.Look(ref plantNegativePrompt, "plantNegativePrompt");
            Scribe_Values.Look(ref mechNegativePrompt, "mechNegativePrompt");
            Scribe_Values.Look(ref entityNegativePrompt, "entityNegativePrompt");
            Scribe_Values.Look(ref otherNegativePrompt, "otherNegativePrompt");
            Scribe_Values.Look(ref aiGenAnimalPreamble, "aiGenAnimalPreamble");
            Scribe_Values.Look(ref aiGenInsectPreamble, "aiGenInsectPreamble");
            Scribe_Values.Look(ref aiGenDragonPreamble, "aiGenDragonPreamble");
            Scribe_Values.Look(ref aiGenAquaticPreamble, "aiGenAquaticPreamble");
            Scribe_Values.Look(ref aiGenPlantPreamble, "aiGenPlantPreamble");
            Scribe_Values.Look(ref aiGenMechPreamble, "aiGenMechPreamble");
            Scribe_Values.Look(ref aiGenEntityPreamble, "aiGenEntityPreamble");
            Scribe_Values.Look(ref aiGenOtherPreamble, "aiGenOtherPreamble");
            // Clearing the avatar cache on every Scribe pass (incl. saves
            // and resolve passes) caused a per-frame texture re-render storm.
            // Only clear after an actual load. See review #4.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Fill empty templates with defaults (fix for saves from older versions)
                if (string.IsNullOrEmpty(aiGenPreamble)) aiGenPreamble = aiGenPreambleDefault;
                if (string.IsNullOrEmpty(aiGenAnimalPreamble)) aiGenAnimalPreamble = aiGenAnimalPreambleDefault;
                if (string.IsNullOrEmpty(aiGenInsectPreamble)) aiGenInsectPreamble = aiGenInsectPreambleDefault;
                if (string.IsNullOrEmpty(aiGenDragonPreamble)) aiGenDragonPreamble = aiGenDragonPreambleDefault;
                if (string.IsNullOrEmpty(aiGenAquaticPreamble)) aiGenAquaticPreamble = aiGenAquaticPreambleDefault;
                if (string.IsNullOrEmpty(aiGenPlantPreamble)) aiGenPlantPreamble = aiGenPlantPreambleDefault;
                if (string.IsNullOrEmpty(aiGenMechPreamble)) aiGenMechPreamble = aiGenMechPreambleDefault;
                if (string.IsNullOrEmpty(aiGenEntityPreamble)) aiGenEntityPreamble = aiGenEntityPreambleDefault;
                if (string.IsNullOrEmpty(aiGenOtherPreamble)) aiGenOtherPreamble = aiGenOtherPreambleDefault;
                AvatarMod.mainManager.SetBGColor(new Color(0,0,0,0));
                AvatarMod.ClearCachedAvatars();
            }
        }
    }

    public enum ApiProvider
    {
        StabilityAI,
        GoogleGemini,
        OpenRouter,
        Pixazo,
        NagaAc,
        Generic
    }

    public enum ArtStyle
    {
        None,
        Realistic,
        Anime,
        ModernAnime,
        Tensura,
        Toriyama,
        Oda,
        Naruto,
        PixelArt,
        Cartoon3D,
        Cartoon2D,
        AvatarCartoon,
        Courage,
        AdventureTime,
        StevenUniverse,
        Simpsons,
        RickMorty,
        StarWarsCartoon,
        Invincible,
        Fantasy,
        ComicBook,
        Frazetta,
        AlanLee,
        Brom,
        Elmore,
        Pathfinder,
        RicardoMango,
        Amano,
        EricaAwano,
        TurmaMonica,
        Moebius,
        FinalFantasy,
        Inomata,
        Noir,
        Steampunk,
        Cyberpunk,
        PopArt,
        Claymation,
        Ukiyoe,
        Custom
    }

    public static class ArtStylePrompts
    {
        public static string GetPrompt(ArtStyle style, string customPrompt)
        {
            switch (style)
            {
                case ArtStyle.Realistic:    return "photorealistic portrait, detailed skin texture, professional studio photography, natural lighting, sharp focus, 8k, dslr";
                case ArtStyle.Anime:        return "anime style portrait, clean lineart, vibrant colors, detailed face, studio ghibli inspired";
                case ArtStyle.ModernAnime:  return "modern anime style portrait, clean crisp lines, vibrant colors, advanced lighting effects, dynamic shading, current anime aesthetic, highly detailed";
                case ArtStyle.PixelArt:     return "pixel art style, 16-bit sprite, retro game character portrait, sharp pixels, flat colors, chunky pixel art";
                case ArtStyle.Cartoon3D:    return "cartoon style, pixar-inspired, 3D rendered character, soft lighting, expressive face, vibrant, cute";
                case ArtStyle.Cartoon2D:    return "cartoon 2D, Cartoon Network style, clean outlines, flat colors, expressive shapes, angular forms, retro CN feel";
                case ArtStyle.Fantasy:      return "fantasy art style, epic, digital painting, card game art character portrait, detailed, oil painting texture, rich colors, D&D style";
                case ArtStyle.ComicBook:    return "western comic book style, bold ink lines, heavy cross-hatching shadows, halftone dot patterns, block colors, classic American graphic novel aesthetic";
                case ArtStyle.Noir:         return "film noir style, black and white portrait, dramatic shadows, high contrast, vintage photography, moody atmosphere";
                case ArtStyle.Steampunk:    return "industrial, clockwork mechanisms, brass, gears, cogs, Victorian tech, brown tones, clockwork, detailed mechanisms";
                case ArtStyle.Cyberpunk:    return "neon, cell-shaded, high-tech urban environment, low-life, glowing elements, Japanese street style, urban chaos, stylized reflection";
                case ArtStyle.PopArt:       return "bold outlines, primary colors, comic book dots, halftone patterns, Andy Warhol style, simplified forms, screen print texture";
                case ArtStyle.Claymation:   return "claymation, physical clay texture, handcrafted feel, stop motion style, fingerprint details, matte finish, soft studio lighting";
                case ArtStyle.Ukiyoe:       return "traditional Japanese woodblock print, bold line work, limited color palette, flat composition, stylized waves, historical feel";
                case ArtStyle.Tensura:        return "That Time I Got Reincarnated as a Slime anime style, colorful fantasy world, expressive characters with large eyes, detailed magical effects, slime-inspired aesthetic, vibrant RPG atmosphere";
                case ArtStyle.Toriyama:       return "Akira Toriyama anime style, Dragon Ball inspired, angular muscular characters, spiky hair, bold action poses, dynamic energy auras, vibrant colors, classic 90s shonen aesthetic";
                case ArtStyle.Oda:            return "Eiichiro Oda anime style, One Piece inspired, exaggerated expressive faces, unique character silhouettes, dynamic comic panel-like composition, bold outlines, adventurous spirit";
                case ArtStyle.Naruto:         return "Naruto anime style, Masashi Kishimoto inspired, detailed ninja aesthetic, chakra energy effects, hand-drawn shading, emotional character expressions, Hidden Leaf Village atmosphere";
                case ArtStyle.AvatarCartoon:  return "Avatar The Last Airbender cartoon style, elemental bending effects, painterly backgrounds, anime-influenced character designs, balanced proportions, spiritual atmosphere";
                case ArtStyle.Courage:        return "Courage the Cowardly Dog cartoon style, surreal horror-comedy aesthetic, distorted character proportions, dark moody backgrounds, bold thick outlines, claymation-like textures";
                case ArtStyle.AdventureTime:  return "Adventure Time cartoon style, simple noodle-limb characters, vibrant pastel colors, whimsical fantasy world, minimalist facial features, playful surreal aesthetic";
                case ArtStyle.StevenUniverse: return "Steven Universe cartoon style, soft rounded character designs, pastel color palette, geometric gem motifs, expressive emotional faces, inclusive diverse characters, space opera aesthetic";
                case ArtStyle.Simpsons:       return "The Simpsons cartoon style, yellow skin characters, overbite profiles, four-fingered hands, bold simple outlines, flat colors, satirical suburban setting, Futurama Disenchantment aesthetic";
                case ArtStyle.RickMorty:      return "Rick and Morty cartoon style, portal gun sci-fi, burping character expressions, dilated pupils, angular thin limbs, interdimensional chaos, adult animated comedy aesthetic";
                case ArtStyle.StarWarsCartoon: return "Star Wars cartoon style, lightsaber glow effects, clone trooper armor detail, galactic backdrop, stylized alien species, space opera animated, droids and starfighters";
                case ArtStyle.Invincible:     return "Invincible comic cartoon style, Cory Walker Ryan Ottley inspired, superhero action with realistic anatomy, bold ink lines, dramatic shadowing, intense fight scene composition";
                case ArtStyle.Frazetta:       return "Frank Frazetta fantasy art style, dramatic chiaroscuro lighting, muscular heroic figures, oil painting texture, dark atmospheric backgrounds, sword and sorcery, pulpy dynamic compositions";
                case ArtStyle.AlanLee:        return "Alan Lee fantasy art style, ethereal watercolor textures, Middle-earth inspired, soft mystical lighting, detailed natural landscapes, elegant elvish aesthetic, Tolkien illustration";
                case ArtStyle.Brom:           return "Gerald Brom fantasy art style, dark gothic atmosphere, haunting oil painting quality, supernatural horror fantasy, rich deep shadows, dramatic figure composition, eerie mystical elements";
                case ArtStyle.Elmore:         return "Larry Elmore fantasy art style, classic D&D illustration, heroic poses, detailed armor and weapons, lush natural backgrounds, warm dramatic lighting, 80s fantasy aesthetic";
                case ArtStyle.Pathfinder:     return "Pathfinder RPG art style, Wayne Reynolds inspired, dynamic action poses, intricate armor designs, bold angular linework, epic fantasy battles, rich saturated colors";
                case ArtStyle.RicardoMango:   return "Ricardo Manga fantasy art style, Brazilian comic influence, detailed character designs, vibrant tropical color palette, dynamic fantasy action, expressive muscular heroes";
                case ArtStyle.EricaAwano:     return "Erica Awano comic art style, elegant detailed linework, manga-influenced fantasy, delicate character features, flowing hair and fabric, ethereal atmosphere";
                case ArtStyle.TurmaMonica:    return "Turma da Monica comic style, Mauricio de Sousa inspired, rounded cute characters, bright primary colors, simple expressive faces, Brazilian childhood nostalgia, wholesome family-friendly aesthetic";
                case ArtStyle.Amano:          return "Yoshitaka Amano fantasy art style, Final Fantasy concept artist, ethereal watercolor washes, delicate flowing lines, otherworldly elegant characters, dreamlike surreal atmosphere, decorative art nouveau influence";
                case ArtStyle.FinalFantasy:   return "Final Fantasy game art style, detailed character designs with elaborate costumes, dramatic lighting, fantasy RPG aesthetic, intricate weapon designs, cinematic composition, Yoshitaka Amano and Tetsuya Nomura influence";
                case ArtStyle.Inomata:        return "Mutsumi Inomata fantasy art style, Tales series aesthetic, elegant flowing character designs, soft watercolor textures, detailed ornate costumes, ethereal magical atmosphere, delicate facial features";
                case ArtStyle.Moebius:        return "Moebius comic art style, Jean Giraud inspired, clean precise linework, surreal sci-fi landscapes, flat subtle color washes, intricate cross-hatching, visionary European bande dessinee aesthetic";
                case ArtStyle.Custom:       return customPrompt ?? "";
                default:                    return "";
            }
        }

        public static string GetNegativePrompt(ArtStyle style)
        {
            switch (style)
            {
                case ArtStyle.Realistic:    return "illustration, painting, drawing, cartoon, anime, comic, sketch, cgi, 3d render, stylized, cel shading";
                case ArtStyle.Anime:        return "photorealistic, 3d render, realistic, western comic style, hyperrealistic, photograph";
                case ArtStyle.ModernAnime:  return "photorealistic, 3d render, realistic, old anime, 90s anime, western comic style, photograph, sketch";
                case ArtStyle.PixelArt:     return "photorealistic, 3d render, smooth, blurry, high resolution, detailed, realistic, painting";
                case ArtStyle.Cartoon3D:    return "photorealistic, realistic, 2d flat, anime, sketch, black and white, dark, horror, gritty";
                case ArtStyle.Cartoon2D:    return "photorealistic, 3d render, realistic, detailed shading, gradients, smooth, cgi, pixar 3d";
                case ArtStyle.Fantasy:      return "photorealistic, modern, minimalist, simple, cartoon, anime, black and white, photograph";
                case ArtStyle.ComicBook:    return "photorealistic, 3d render, cgi, digital painting, smooth gradients, realistic shading, manga, anime";
                case ArtStyle.Noir:         return "color, vibrant, bright, saturated, cartoon, anime, 3d render, digital art, cheerful, daylight";
                case ArtStyle.Steampunk:    return "modern, sleek, minimalist, plastic, digital, neon, futuristic scifi, bright colors, clean";
                case ArtStyle.Cyberpunk:    return "medieval, rustic, natural, pastoral, vintage, black and white, minimalist, clean, organic";
                case ArtStyle.PopArt:       return "photorealistic, 3d render, smooth gradients, subtle colors, traditional painting, realistic shading";
                case ArtStyle.Claymation:   return "digital art, smooth, glossy, photorealistic, cgi, 3d render, shiny, polished, liquid, metallic";
                case ArtStyle.Ukiyoe:       return "photorealistic, 3d render, western art style, perspective depth, realistic lighting, oil painting, modern";
                case ArtStyle.Tensura:        return "photorealistic, 3d render, realistic, western cartoon, adult swim style, dark horror, gritty, photograph";
                case ArtStyle.Toriyama:       return "photorealistic, 3d render, realistic, slice of life anime, moe style, photograph, western cartoon";
                case ArtStyle.Oda:            return "photorealistic, 3d render, realistic, generic anime, moe style, photograph, minimal detail, simple shapes";
                case ArtStyle.Naruto:         return "photorealistic, 3d render, realistic, western cartoon, slice of life, photograph, minimal design, no action";
                case ArtStyle.AvatarCartoon:  return "photorealistic, 3d render, anime, western sitcom style, adult cartoon, photograph, dark gritty, horror";
                case ArtStyle.Courage:        return "photorealistic, 3d render, anime, cute wholesome style, bright cheerful, photograph, normal proportions, realistic";
                case ArtStyle.AdventureTime:  return "photorealistic, 3d render, anime, realistic proportions, complex shading, adult cartoon, gritty, dark, detailed";
                case ArtStyle.StevenUniverse: return "photorealistic, 3d render, anime, angular character designs, dark gritty, realistic body proportions, adult cartoon, horror";
                case ArtStyle.Simpsons:       return "photorealistic, 3d render, anime, realistic anatomy, dark gritty, adult drama, proportional features, detailed shading";
                case ArtStyle.RickMorty:      return "photorealistic, 3d render, anime, wholesome family cartoon, realistic anatomy, clean neat, minimal sci-fi, no portal guns";
                case ArtStyle.StarWarsCartoon: return "photorealistic, 3d render, anime, live action, disney 3d, dark gritty, realistic lighting, no lightsabers";
                case ArtStyle.Invincible:     return "photorealistic, 3d render, anime, manga, cute cartoon, comedy style, simple flat, no action, peaceful, minimal shading";
                case ArtStyle.Frazetta:       return "photorealistic, modern, minimalist, simple, cartoon, anime, clean, bright, digital vector, photograph, abstract";
                case ArtStyle.AlanLee:        return "photorealistic, modern, cartoon, anime, digital painting, hard edges, bold colors, abstract, photograph";
                case ArtStyle.Brom:           return "photorealistic, modern, cartoon, anime, bright cheerful, clean, minimalist, digital vector, photograph, light atmosphere";
                case ArtStyle.Elmore:         return "photorealistic, modern, cartoon, anime, minimalist, simple, dark horror, digital vector, photograph, abstract";
                case ArtStyle.Pathfinder:     return "photorealistic, modern, cartoon, anime, minimalist, simple, static poses, no armor, photograph";
                case ArtStyle.RicardoMango:   return "photorealistic, 3d render, anime, manga black white, european comic, minimalist, simple, photograph, no action";
                case ArtStyle.EricaAwano:     return "photorealistic, 3d render, western comic, american cartoon, bold simple, no detail, photograph, digital vector";
                case ArtStyle.TurmaMonica:    return "photorealistic, 3d render, anime, manga, realistic proportions, adult cartoon, dark gritty, horror, detailed";
                case ArtStyle.Amano:          return "photorealistic, 3d render, cartoon, anime cell shaded, hard edges, bold colors, photograph, digital vector, simple";
                case ArtStyle.FinalFantasy:   return "photorealistic, photograph, cartoon, anime cell shade, simple design, no detail, western comic, sketch, minimal";
                case ArtStyle.Inomata:        return "photorealistic, 3d render, western comic, cartoon, simple, no detail, photograph, hard edges, bold colors";
                case ArtStyle.Moebius:        return "photorealistic, 3d render, cartoon, anime, messy sketch, blurred, chaotic composition, photograph, digital painting mess";
                case ArtStyle.Custom:       return "";
                default:                    return "";
            }
        }

        public static string GetLabel(ArtStyle style)
        {
            switch (style)
            {
                case ArtStyle.None:         return "None (no style)";
                case ArtStyle.Realistic:    return "Realistic";
                case ArtStyle.Anime:        return "Anime (Classic / Ghibli)";
                case ArtStyle.ModernAnime:  return "Modern Anime";
                case ArtStyle.PixelArt:     return "Pixel Art";
                case ArtStyle.Cartoon3D:    return "Cartoon 3D / Pixar";
                case ArtStyle.Cartoon2D:    return "Cartoon 2D / Cartoon Network";
                case ArtStyle.Fantasy:      return "Fantasy RPG / High Fantasy";
                case ArtStyle.ComicBook:    return "Comic Book / HQ";
                case ArtStyle.Noir:         return "Noir";
                case ArtStyle.Steampunk:    return "Steampunk";
                case ArtStyle.Cyberpunk:    return "Stylized Cyberpunk";
                case ArtStyle.PopArt:       return "Pop Art";
                case ArtStyle.Claymation:   return "Claymation / Stop Motion";
                case ArtStyle.Ukiyoe:       return "Ukiyo-e";
                case ArtStyle.Tensura:        return "Anime (Tensura / Slime)";
                case ArtStyle.Toriyama:       return "Anime (Akira Toriyama / DBZ)";
                case ArtStyle.Oda:            return "Anime (Eiichiro Oda / One Piece)";
                case ArtStyle.Naruto:         return "Anime (Naruto / Kishimoto)";
                case ArtStyle.AvatarCartoon:  return "Cartoon (Avatar / Korra)";
                case ArtStyle.Courage:        return "Cartoon (Courage the Cowardly Dog)";
                case ArtStyle.AdventureTime:  return "Cartoon (Adventure Time)";
                case ArtStyle.StevenUniverse: return "Cartoon (Steven Universe)";
                case ArtStyle.Simpsons:       return "Cartoon (Simpsons / Futurama)";
                case ArtStyle.RickMorty:      return "Cartoon (Rick and Morty)";
                case ArtStyle.StarWarsCartoon: return "Cartoon (Star Wars)";
                case ArtStyle.Invincible:     return "Cartoon (Invincible)";
                case ArtStyle.Frazetta:       return "Fantasy (Frank Frazetta)";
                case ArtStyle.AlanLee:        return "Fantasy (Alan Lee / Tolkien)";
                case ArtStyle.Brom:           return "Fantasy (Gerald Brom)";
                case ArtStyle.Elmore:         return "Fantasy (Larry Elmore / D&D)";
                case ArtStyle.Pathfinder:     return "Fantasy (Pathfinder RPG)";
                case ArtStyle.RicardoMango:   return "Fantasy (Ricardo Manga)";
                case ArtStyle.EricaAwano:     return "Comic (Erica Awano)";
                case ArtStyle.TurmaMonica:    return "Comic (Turma da Mônica)";
                case ArtStyle.Amano:          return "Fantasy (Yoshitaka Amano)";
                case ArtStyle.FinalFantasy:   return "Game (Final Fantasy)";
                case ArtStyle.Inomata:        return "Game (Mutsumi Inomata / Tales)";
                case ArtStyle.Moebius:        return "Comic (Moebius / Giraud)";
                case ArtStyle.Custom:       return "Custom (user prompt)";
                default:                    return "Unknown";
            }
        }
    }
}
