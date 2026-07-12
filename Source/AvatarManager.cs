#if !v1_3
#define BIOTECH
#endif
#if !(v1_3 || v1_4)
#define ANOMALY
#endif
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public class AvatarManager
    {
        public static AvatarMod mod;
        public Pawn pawn;
        private Feature feature;
        private Texture2D canvas;
        private Texture2D avatar;
        private bool _drawHeadgear = true;
        private bool _drawClothes = true;
        public bool drawHeadgear { get => this._drawHeadgear;
            set
            {
                if (this._drawHeadgear != value)
                {
                    this._drawHeadgear = value;
                    ClearCachedAvatar();
                }
            }
        }
        public bool drawClothes { get => this._drawClothes;
            set
            {
                if (this._drawClothes != value)
                {
                    this._drawClothes = value;
                    ClearCachedAvatar();
                }
            }
        }
        private bool checkDowned = false;
        private Color bgColor = new Color(.5f,.5f,.6f,.5f);
        private int lastUpdateTime;
        private bool updateQueued = false;
        public Texture2D staticTexture;
        private DateTime? staticTextureLastModified;
        private int staticTextureLastCheck;
        public virtual void ClearCachedAvatar()
        {
            if (avatar != null)
            {
                // cap the update frequency
                if (Time.frameCount > lastUpdateTime + 5)
                { // destroy old texture
                    UnityEngine.Object.Destroy(avatar);
                    avatar = null;
                    feature = null;
                    updateQueued = false;
                }
                else
                {
                    updateQueued = true;
                }
            }
        }
        public void SetPawn(Pawn pawn)
        {
            if (this.pawn != pawn)
            {
                this.pawn = pawn;
                drawHeadgear = mod.settings.defaultDrawHeadgear;
                ClearCachedAvatar();
                // Also clear the static portrait texture so we load the correct one for this pawn
                if (staticTexture != null)
                {
                    UnityEngine.Object.Destroy(staticTexture);
                    staticTexture = null;
                    staticTextureLastModified = null;
                }
            }
        }
        public void SetBGColor(Color color)
        {
            if (bgColor != color)
            {
                bgColor = color;
                ClearCachedAvatar();
            }
        }
        public void SetCheckDowned(bool checkDowned)
        {
            if (this.checkDowned != checkDowned)
            {
                this.checkDowned = checkDowned;
                ClearCachedAvatar();
            }
        }
        private int Seed()
        {
            #if ANOMALY
            if (pawn.IsDuplicate && Find.PawnDuplicator.duplicates.ContainsKey(pawn.duplicate.duplicateOf))
            {
                Pawn original = Find.PawnDuplicator.duplicates[pawn.duplicate.duplicateOf].First();
                return 2632*original.ageTracker.BirthDayOfYear+3341*original.ageTracker.BirthYear;
            }
            #endif
            return 2632*pawn.ageTracker.BirthDayOfYear+3341*pawn.ageTracker.BirthYear;
        }
        private Feature GetFeature()
        {
            if (feature == null)
            {
                int v = Seed();
                feature = new ((v%450)/90+1, (v%90)/15+1, (v%15)/3+1, (v%3)+1);
            }
            return feature;
        }
        private string GetStardardHead()
        {
            switch ((Seed() % 2700)/450)
            {
                case 1: return "AverageWide";
                case 2: return "AveragePointy";
                case 3: return "NarrowNormal";
                case 4: return "NarrowWide";
                case 5: return "NarrowPointy";
                default: return "AverageNormal";
            }
        }
        private string GetPath<T>(string gender, string lifeStage, string typeName, string fallbackPath) where T: AvatarDef
        {
            string result = null;
            foreach (T def in DefDatabase<T>.AllDefs)
                if (def.typeName == typeName)
                    result = def.GetPath(gender, lifeStage);
            return result ?? fallbackPath;
        }
        private string GetPathByDefName<T>(string gender, string lifeStage, string defName, string fallbackPath) where T: AvatarDef
        {
            return DefDatabase<T>.GetNamedSilentFail(defName)?.GetPath(gender, lifeStage) ?? fallbackPath;
        }
        private T GetApparelDef<T>(Apparel apparel) where T: AvatarApparelDef
        {
            T def = null;
            CompStyleable comp = apparel.GetComp<CompStyleable>();
            if (comp != null && comp.styleDef != null)
                def = DefDatabase<T>.GetNamedSilentFail(comp.styleDef.defName);
            return def ?? DefDatabase<T>.GetNamedSilentFail(apparel.def.defName);
        }
        #if !(v1_3 || v1_4)
        private List<AvatarLayer> HandleRenderNode(List<PawnRenderNodeProperties> props, Hediff hediff, string path, Color skinColor, Color hairColor)
        {
            List<AvatarLayer> layers = new ();
            foreach (PawnRenderNodeProperties prop in props)
            {
                AvatarLayer attachment = new (path);
                if (prop.flipGraphic == true)
                    attachment.flipGraphic = true;
                if (prop.texPaths != null)
                {
                    PawnRenderNode node = new (pawn, prop, null);
                    node.hediff = hediff;
                    string variant = node.TexPathFor(pawn); // should end with A, B, C
                    attachment.texPath += variant[variant.Length-1];
                }
                if (prop.colorType == PawnRenderNodeProperties.AttachmentColorType.Skin)
                    attachment.color = skinColor;
                if (prop.colorType == PawnRenderNodeProperties.AttachmentColorType.Hair)
                    attachment.color = hairColor;
                layers.Add(attachment);
            }
            return layers;
        }
        #endif
        private bool ShouldShowWrinkles()
        {
            float lifeExpectancy = pawn.RaceProps.lifeExpectancy;
            #if BIOTECH
            foreach (Gene gene in pawn.genes.GenesListForReading.Where(g => g.Active))
            {
                if (gene.def.defName == "Ageless")
                    lifeExpectancy = float.PositiveInfinity;
                else if (!gene.def.statFactors.NullOrEmpty())
                {
                    foreach (StatModifier statModifier in gene.def.statFactors)
                    {
                        if (statModifier.stat == StatDefOf.LifespanFactor)
                            lifeExpectancy *= statModifier.value;
                    }
                }
            }
            #endif
            return pawn.ageTracker.AgeBiologicalYears >= 0.7 * lifeExpectancy;
        }
        #if BIOTECH
        private int GeneUseColor(Gene gene)
        {
            #if v1_4
            return gene.def.HasGraphic ? (int) gene.def.graphicData.colorType : -1;
            #else
            return gene.def.renderNodeProperties?.Count >= 1 ? (int) gene.def.renderNodeProperties[0].colorType : -1;
            #endif
        }
        #endif
        private Texture2D RenderAvatar()
        {
            lastUpdateTime = Time.frameCount;
            int width = 40;
            int height = 48;
            int halfWidthHeightDiff = (height-width)/2;
            if (canvas == null)
                canvas = new (width, height);
            TextureUtil.ClearTexture(canvas, bgColor);
            string gender = (pawn.gender == Gender.Female) ? "Female" : "Male";
            string lifeStage = "";
            int yOffset = 0;
            int eyeLevel = 0;
            string raceName = pawn.RaceProps.AnyPawnKind?.race.defName ?? "Human";
            raceName = (raceName == "Human") ? "" : ("_" + raceName);
            if (pawn.ageTracker.CurLifeStage.defName == "HumanlikeBaby"
                || pawn.ageTracker.CurLifeStage.defName == "HumanlikeToddler") // from Toddlers mod
            {
                lifeStage = "Newborn";
                yOffset = 3;
                eyeLevel = -2;
            }
            else if (pawn.ageTracker.CurLifeStage.defName == "HumanlikeChild" || pawn.ageTracker.CurLifeStage.defName == "HumanlikePreTeenager")
            {
                lifeStage = "Child";
                yOffset = 2;
                eyeLevel = -1;
            }
            // babies are always downed, no need to draw them this way unless dead
            bool downed = checkDowned && (lifeStage != "Newborn" && pawn.Downed);
            int downedOffset = 10;
            Color skinColor = pawn.story.SkinColor;
            Color hairColor = pawn.story.hairColor;
            #if ANOMALY
            if (pawn.IsShambler) hairColor = MutantUtility.GetShamblerColor(hairColor);
            #endif
            List<AvatarLayer> layers = new ();
            AvatarLayer coversAll = null;
            #if BIOTECH
            List<Gene> activeGenes = pawn.genes.GenesListForReading.Where(g => g.Active).ToList();
            // collect all cosmetic genes not handled by the defined defs
            List<Gene> cosmeticGenes = activeGenes.Where(g =>
                !DefDatabase<AvatarGeneDef>.AllDefsListForReading.Exists(def => def.geneName == g.def.defName && def.replaceModdedTexture)
                #if v1_4
                && g.def.HasGraphic
                && g.def.graphicData.drawLoc != GeneDrawLoc.Tailbone
                && !g.def.graphicData.drawOnEyes // eye textures cause most issues, easier to just ignore them
                #else
                && g.def.renderNodeProperties?.Count == 1
                && g.def.renderNodeProperties[0].parentTagDef?.defName != "Body"
                #endif
                ).ToList();
            #endif
            #if v1_3
            string headTypeName = pawn.story.HeadGraphicPath.Split('/').Last();
            headTypeName = headTypeName.Remove(headTypeName.LastIndexOf('_'), 1);
            #else
            string headTypeName = pawn.story.headType.defName;
            #endif
            if (headTypeName.StartsWith("Female_"))
                headTypeName = headTypeName.Substring(7);
            if (headTypeName.EndsWith("_Female"))
                headTypeName = headTypeName.Substring(0, headTypeName.Length-7);
            if (headTypeName.StartsWith("Male_"))
                headTypeName = headTypeName.Substring(5);
            if (headTypeName.EndsWith("_Male"))
                headTypeName = headTypeName.Substring(0, headTypeName.Length-5);
            if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Dessicated)
            {
                headTypeName = "Skeleton";
                skinColor = new (0.8f, 0.7f, 0.6f);
            }
            bool hideTattoo = false;
            bool hideWrinkles = false;
            bool hideEyes = false;
            bool hideEars = false;
            bool hideNose = false;
            bool hideMouth = false;
            bool hideHair = false;
            bool hideBeard = false;
            bool specialNoJaw = false;
            int hairHideTop = 0;
            int headHideTop = 0;
            int headAttachmentOffset = 0;
            string bodyTypeName = "";
            List<EyePos> eyesPos = new List<EyePos> {new EyePos (14,27,15,27), new EyePos (24,27,23,27)};
            AvatarHeadDef headTypeDef = DefDatabase<AvatarHeadDef>.GetNamedSilentFail("Head_" + headTypeName + raceName);
            string facePaint = null;
            Color? facePaintColor = null;
            if (headTypeDef != null)
            {
                hideTattoo = headTypeDef.hideTattoo;
                hideHair = headTypeDef.hideHair;
                hideBeard = headTypeDef.hideBeard;
                hideWrinkles = headTypeDef.hideWrinkles;
                hideEyes = headTypeDef.hideEyes;
                hideEars = headTypeDef.hideEars;
                hideNose = headTypeDef.hideNose;
                hideMouth = headTypeDef.hideMouth;
                bodyTypeName = headTypeDef.forceBodyType;
                specialNoJaw = headTypeDef.specialNoJaw;
                facePaint = headTypeDef.facePaint;
                facePaintColor = headTypeDef.facePaintColor;
                headAttachmentOffset = headTypeDef.headAttachmentOffset;
                if (headTypeDef.eyesPos != null)
                    eyesPos = headTypeDef.eyesPos;
                if (headTypeDef.reassignStandard)
                    headTypeName = GetStardardHead();
            }
            List<(Apparel, AvatarBodygearDef)> bodygears = new ();
            List<(Apparel, AvatarBackgearDef)> backgears = new ();
            List<(Apparel, AvatarFacegearDef)> facegears = new ();
            List<(Apparel, AvatarHeadgearDef)> headgears = new ();
            if (drawClothes)
            {
                foreach (Apparel apparel in pawn.apparel.WornApparel)
                {
                    if (GetApparelDef<AvatarBodygearDef>(apparel) is AvatarBodygearDef bodygearDef)
                        bodygears.Add((apparel, bodygearDef));
                    else if (GetApparelDef<AvatarBackgearDef>(apparel) is AvatarBackgearDef backgearDef)
                        backgears.Add((apparel, backgearDef));
                    else if (GetApparelDef<AvatarFacegearDef>(apparel) is AvatarFacegearDef facegearDef)
                    {
                        if (drawHeadgear)
                        {
                            facegears.Add((apparel, facegearDef));
                            if (mod.settings.showHairWithHeadgear)
                                hideHair |= facegearDef.hideHair;
                            else
                                hideHair |= apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "UpperHead" || p.defName == "FullHead");
                            hairHideTop = Math.Max(hairHideTop, facegearDef.hideTop);
                            headHideTop = Math.Max(headHideTop, facegearDef.hideTop);
                            hideBeard |= facegearDef.hideBeard;
                        }
                    }
                    else if (GetApparelDef<AvatarHeadgearDef>(apparel) is AvatarHeadgearDef headgearDef)
                    {
                        if (drawHeadgear)
                        {
                            #if v1_3 || v1_4
                            if (apparel.def.apparel.shellCoversHead)
                            #else
                            if (apparel.def.apparel.renderSkipFlags?.FirstOrDefault()?.defName == "Head")
                            #endif
                                coversAll = new (headgearDef.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset);
                            else
                                headgears.Add((apparel, headgearDef));
                            if (mod.settings.showHairWithHeadgear)
                                hideHair |= headgearDef.hideHair;
                            else
                                hideHair |= apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "UpperHead" || p.defName == "FullHead");
                            hairHideTop = Math.Max(hairHideTop, headgearDef.hideTop);
                            headHideTop = Math.Max(headHideTop, headgearDef.hideTop);
                            hideBeard |= headgearDef.hideBeard;
                        }
                    }
                    else if (apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "Torso")
                        && apparel.def.thingCategories != null) // warcaskets don't have this...
                    {
                        if (apparel.def.thingCategories.Exists(p => p.defName == "ApparelArmor"))
                            bodygears.Add((apparel, DefDatabase<AvatarBodygearDef>.GetNamedSilentFail("Avatar_GenericArmor")));
                        else if (!apparel.def.thingCategories.Exists(p => p.defName == "ApparelUtility"))
                            bodygears.Add((apparel, DefDatabase<AvatarBodygearDef>.GetNamedSilentFail("Avatar_Generic")));
                    }
                }
            }
            // sorting
            // Vanilla apparels are already sorted.
            // However the offset can be set manually which changes rendering order.
            #if v1_3 || v1_4
            if (ModCompatibility.VanillaFactionsExpanded_Loaded)
            {
                bodygears = bodygears.OrderBy(a => ModCompatibility.GetVEOffset(a.Item1.def)).ToList();
            }
            #else
            bodygears = bodygears.OrderBy(a => a.Item1.def.apparel?.drawData?.dataSouth?.offset?.y ?? 0f).ToList();
            #endif
            // building layers
            #if BIOTECH
            foreach (Gene gene in activeGenes)
            {
                foreach (AvatarBackDef def in DefDatabase<AvatarBackDef>.AllDefs)
                    if (gene.def.defName == def.geneName)
                        layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage)));
            }
            #endif
            foreach ((Apparel apparel, AvatarBackgearDef def) in backgears)
            {
                layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, 8));
            }
            List<AvatarLayer> body_layers = new ();
            string neckPath = GetPathByDefName<AvatarBodyDef>(gender, lifeStage, "Body_" + bodyTypeName, "Core/"+gender+lifeStage+"/Neck");
            // asimov colored robot support
            AvatarLayer neck = new (neckPath, skinColor, 8);
            if (ModCompatibility.Asimov_Loaded)
            {
                if (ModCompatibility.GetAsimovSkinColor(pawn) is (Color skinFirst, Color skinSecond))
                {
                    neck.color = skinFirst;
                    neck.colorB = skinSecond;
                }
            }
            body_layers.Add(neck);
            if (!hideTattoo)
            {
                string bodyTattooPath = GetPath<AvatarBodyTattooDef>(gender, lifeStage, pawn.style.BodyTattoo?.defName, null);
                body_layers.Add(new AvatarLayer(bodyTattooPath, new Color(1f,1f,1f,0.8f), 8));
            }
            #if ANOMALY
            if (pawn.IsShambler && !(headTypeName == "Skeleton") && !mod.settings.noCorpseGore)
            {
                body_layers.Add(new AvatarLayer("Core/Unisex/Corpse/BodyScar" + ((Seed()%125)/25+1).ToString(), skinColor, 8));
            }
            #endif
            if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting && !mod.settings.noCorpseGore)
            {
                layers.Add(new ("Core/Unisex/Corpse/Skeleton" + lifeStage, new (0.8f, 0.7f, 0.6f), 8));
                foreach (AvatarLayer layer in body_layers)
                {
                    layer.alphaMaskPath = "Core/Unisex/Corpse/BodyMask" + ((Seed()%625)/125+1).ToString();
                }
            }
            foreach (AvatarLayer layer in body_layers)
                layers.Add(layer);
            foreach ((Apparel apparel, AvatarBodygearDef def) in bodygears)
            {
                layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, 8));
            }
            foreach (Hediff h in pawn.health.hediffSet.hediffs.Where(h => h.Part != null))
            {
                foreach (AvatarBodyHediffDef def in DefDatabase<AvatarBodyHediffDef>.AllDefs)
                {
                    if (h.def.defName == def.typeName)
                    {
                        AvatarLayer prosthetic = new (def.GetPath(gender, lifeStage));
                        prosthetic.offset = 8;
                        if (h.Part.def.defName == "Shoulder")
                        {
                            prosthetic.color = skinColor;
                            if (h.Part.woundAnchorTag == "LeftShoulder")
                                prosthetic.drawDexter = false;
                            else
                                prosthetic.drawSinister = false;
                            layers.Add(prosthetic);
                        }
                        else
                        {
                            #if v1_3 || v1_4
                            layers.Add(prosthetic);
                            #else
                            if (h.def.renderNodeProperties != null)
                            {
                                foreach (AvatarLayer layer in HandleRenderNode(h.def.renderNodeProperties, h, def.GetPath(gender, lifeStage), skinColor, hairColor))
                                {
                                    layer.offset = 8;
                                    layers.Add(layer);
                                }
                            }
                            else
                                layers.Add(prosthetic);
                            #endif
                        }
                    }
                }
            }
            // draw head
            if (!pawn.health.hediffSet.hediffs.Exists(h => h.def.defName == "MissingBodyPart" && h.Part != null && h.Part.def.defName == "Head"))
            {
                string headPath = GetPathByDefName<AvatarHeadDef>(gender, lifeStage, "Head_" + headTypeName + raceName, "Core/"+gender+lifeStage+"/Head/AverageNormal");
                string faceTattooPath = GetPath<AvatarFaceTattooDef>(gender, lifeStage, pawn.style.FaceTattoo?.defName, null);
                string beardPath = GetPathByDefName<AvatarBeardDef>(gender, lifeStage, "Beard_" + (pawn.style.beardDef?.defName ?? "NoBeard"), "BEARD");
                string hairPath = GetPathByDefName<AvatarHairDef>(gender, lifeStage, "Hair_" + (pawn.story.hairDef?.defName ?? "Bald"), "HAIR");
                string earsPath = "Core/Unisex/Ears/Ears_Human";
                string nosePath = "Core/"+gender+lifeStage+"/Nose/Nose"+GetFeature().nose.ToString();
                string eyesPath = "Core/"+gender+lifeStage+"/Eyes/Eyes"+GetFeature().eyes.ToString();
                string mouthPath = "Core/"+(mod.settings.noFemaleLips ? "Male" : gender)+lifeStage+"/Mouth/Mouth"+GetFeature().mouth.ToString();
                string browsPath = "Core/"+gender+lifeStage+"/Brows/Brows"+GetFeature().brows.ToString();
                (Color, Color) eyeColor = (new Color(.6f,.6f,.6f,1), new Color(.1f,.1f,.1f,1));
                Color earsColor = skinColor;
                Color noseColor = skinColor;
                List<AvatarLayer> head_layers = new ();
                #if BIOTECH
                foreach (Gene gene in activeGenes)
                {
                    foreach (AvatarEarsDef def in DefDatabase<AvatarEarsDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            earsPath = def.GetPath(gender, lifeStage);
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    earsColor = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    earsColor = hairColor;
                                    break;
                            }
                        }
                    foreach (AvatarNoseDef def in DefDatabase<AvatarNoseDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            nosePath = def.GetPath(gender, lifeStage);
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    noseColor = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    noseColor = hairColor;
                                    break;
                            }
                        }
                    foreach (AvatarMouthDef def in DefDatabase<AvatarMouthDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                            mouthPath = def.GetPath(gender, lifeStage);
                    foreach (AvatarBrowsDef def in DefDatabase<AvatarBrowsDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                            browsPath = def.GetPath(gender, lifeStage);
                    foreach (AvatarEyesDef def in DefDatabase<AvatarEyesDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            eyesPath = def.GetPath(gender, lifeStage) ?? eyesPath;
                            eyeColor = (def.color1 ?? eyeColor.Item1, def.color2 ?? eyeColor.Item2);
                            eyesPos = def.eyesPos ?? eyesPos;
                        }
                }
                #endif
                if (pawn.story.traits.allTraits.Exists(t => t.def.defName == "BodyMastery")
                    || pawn.health.hediffSet.hediffs.Exists(t => t.def.defName == "VoidTouched"))
                    eyeColor = (new Color(0.7f, 0.7f, 0.7f), new Color(1f, 1f, 1f));
                AvatarLayer ears = new (earsPath, earsColor);
                AvatarLayer nose = new (nosePath, noseColor);
                #if BIOTECH
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.endogeneCategory == EndogeneCategory.Ears)
                    {
                        ears = AvatarLayer.FromGene(gene, pawn);
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                    else if (gene.def.endogeneCategory == EndogeneCategory.Nose)
                    {
                        nose = AvatarLayer.FromGene(gene, pawn);
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting
                    #if ANOMALY
                    || pawn.IsShambler
                    #endif
                )
                {
                    // try to make the eyes look more lifeless
                    Color avg = new ((eyeColor.Item1.r + eyeColor.Item2.r*2)/3, (eyeColor.Item1.g + eyeColor.Item2.g*2)/3, (eyeColor.Item1.b + eyeColor.Item2.b*2)/3);
                    eyeColor.Item1 = avg;
                    eyeColor.Item2 = avg;
                }
                AvatarLayer eyes = new (eyesPath, skinColor);
                eyes.eyeColor = eyeColor;
                AvatarLayer mouth = new (mouthPath, skinColor);
                if (mod.settings.noFemaleLips && gender == "Female" && lifeStage != "Newborn") mouth.offset = -1; // shift female lips
                AvatarLayer head = new (headPath, skinColor);
                head.hideTop = headHideTop + headAttachmentOffset;
                // asimov colored robot support
                if (ModCompatibility.Asimov_Loaded)
                {
                    if (ModCompatibility.GetAsimovSkinColor(pawn) is (Color skinFirst, Color skinSecond))
                    {
                        head.color = skinFirst;
                        head.colorB = skinSecond;
                    }
                }
                if (!hideEars && (!mod.settings.earsOnTop || ears.texPath == "Core/Unisex/Ears/Ears_Human")) layers.Add(ears);
                // start to build head layers
                head_layers.Add(head);
                if (!hideWrinkles && !mod.settings.noWrinkles)
                {
                    if (ShouldShowWrinkles())
                        head_layers.Add(new AvatarLayer("Core/Unisex/Facial/Wrinkles", skinColor));
                }
                #if BIOTECH
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.endogeneCategory == EndogeneCategory.Jaw
                        #if v1_4
                        || gene.def.graphicData.layer == GeneDrawLayer.PostSkin
                        #endif
                        )
                    {
                        head_layers.Add(AvatarLayer.FromGene(gene, pawn));
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                if (!hideMouth) head_layers.Add(mouth);
                if (!hideNose) head_layers.Add(nose);
                if (!hideEyes) head_layers.Add(eyes);
                #if BIOTECH
                foreach (AvatarFacialDef def in DefDatabase<AvatarFacialDef>.AllDefs)
                {
                    foreach (Gene gene in activeGenes)
                    {
                        // handle variants
                        if (gene.def.defName == def.geneName)
                        {
                            string path = def.GetPath(gender, lifeStage);
                            #if v1_4
                            if (gene.def.graphicData != null && gene.def.graphicData.graphicPaths != null)
                            {
                                string variant = gene.def.graphicData.GraphicPathFor(pawn); // should end with A, B, C
                            #else
                            if (gene.def.renderNodeProperties?.Count == 1 && gene.def.renderNodeProperties[0].texPaths != null)
                            {
                                PawnRenderNode node = new (pawn, gene.def.renderNodeProperties[0], null);
                                node.gene = gene;
                                string variant = node.TexPathFor(pawn); // should end with A, B, C
                            #endif
                                path += variant[variant.Length-1];
                            }
                            Color color = skinColor;
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    color = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    color = hairColor;
                                    break;
                            }
                            head_layers.Add(new AvatarLayer(path, color));
                        }
                    }
                }
                #endif
                if (!string.IsNullOrEmpty(facePaint))
                {
                    string facePaintPath = GetPathByDefName<AvatarFacePaintDef>(gender, lifeStage, facePaint, null);
                    foreach (AvatarLayer layer in head_layers)
                    {
                        layer.maskPath = facePaintPath;
                        layer.colorB = facePaintColor;
                    }
                }
                if (!hideTattoo)
                    head_layers.Add(new AvatarLayer(faceTattooPath, new Color(1f,1f,1f,0.8f), headAttachmentOffset));
                #if ANOMALY
                if (pawn.IsShambler && !(headTypeName == "Skeleton") && !mod.settings.noCorpseGore)
                {
                    head_layers.Add(new AvatarLayer("Core/Unisex/Corpse/FaceScar" + ((Seed()%25)/5+1).ToString(), skinColor));
                }
                #endif
                if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting && !mod.settings.noCorpseGore)
                {
                    layers.Add(new ("Core/Unisex/Corpse/Skull" + lifeStage, new (0.8f, 0.7f, 0.6f)));
                    foreach (AvatarLayer layer in head_layers)
                    {
                        layer.alphaMaskPath = "Core/Unisex/Corpse/FaceMask" + ((Seed()%5)+1).ToString();
                    }
                }
                foreach (AvatarLayer layer in head_layers)
                    layers.Add(layer);
                foreach (Hediff h in pawn.health.hediffSet.hediffs.Where(h => h.Part != null))
                {
                    if (h is Hediff_MissingPart _)
                    {
                        if (h.Part.def.defName == "Nose")
                        {
                            nose.texPath = "Core/Unisex/Nose/Missing" + lifeStage;
                        }
                        else if (h.Part.def.defName == "Jaw")
                        {
                            if (specialNoJaw)
                                head.texPath += "NoJaw";
                            else
                                layers.Add(new AvatarLayer("Core/Unisex/Jaw/Missing" + lifeStage, skinColor));
                        }
                        else if (h.Part.def.defName == "Eye")
                        {
                            AvatarLayer missingEyes = new ("Core/Unisex/Eyes/Missing", skinColor);
                            if (h.Part.woundAnchorTag == "LeftEye") // this means it's left...
                                missingEyes.drawDexter = false;
                            else
                                missingEyes.drawSinister = false;
                            layers.Add(missingEyes);
                        }
                        else if (h.Part.def.defName == "Ear")
                        {
                            #if v1_3 || v1_4
                            if (h.Part.customLabel == "left ear")
                            #else
                            if (h.Part.flipGraphic)
                            #endif
                                ears.drawSinister = false;
                            else
                                ears.drawDexter = false;
                        }
                    }
                    else if (h is Hediff_AddedPart _)
                    {
                        foreach (AvatarHeadHediffDef def in DefDatabase<AvatarHeadHediffDef>.AllDefs)
                        {
                            if (h.def.defName == def.typeName)
                            {
                                if (h.Part.def.defName == "Nose")
                                {
                                    nose.texPath = def.GetPath(gender, lifeStage);
                                    nose.color = null;
                                }
                                else
                                {
                                    AvatarLayer prosthetic = new (def.GetPath(gender, lifeStage));
                                    if (h.Part.def.defName == "Eye")
                                    {
                                        if (h.Part.woundAnchorTag == "LeftEye")
                                            prosthetic.drawDexter = false;
                                        else
                                            prosthetic.drawSinister = false;
                                        if (lifeStage != "") prosthetic.offset = -1;
                                        layers.Add(prosthetic);
                                    }
                                    else if (h.Part.def.defName == "Ear")
                                    {
                                        #if v1_3 || v1_4
                                        if (h.Part.customLabel == "left ear")
                                        #else
                                        if (h.Part.flipGraphic)
                                        #endif
                                        {
                                            ears.drawSinister = false;
                                            prosthetic.drawDexter = false;
                                        }
                                        else
                                        {
                                            ears.drawDexter = false;
                                            prosthetic.drawSinister = false;
                                        }
                                        layers.Add(prosthetic);
                                    }
                                    else
                                    {
                                        #if v1_3 || v1_4
                                        layers.Add(prosthetic);
                                        #else
                                        if (h.def.renderNodeProperties != null)
                                            foreach (AvatarLayer layer in HandleRenderNode(h.def.renderNodeProperties, h, def.GetPath(gender, lifeStage), skinColor, hairColor))
                                                layers.Add(layer);
                                        else
                                            layers.Add(prosthetic);
                                        #endif
                                    }
                                }
                            }
                        }
                    }
                    else if (h is Hediff_Injury injury && injury.IsPermanent() && pawn.Drawer.renderer.CurRotDrawMode != RotDrawMode.Dessicated)
                    {
                        string scarName = h.Part.def.defName + "_" + h.def.defName;
                        foreach (AvatarHeadHediffDef def in DefDatabase<AvatarHeadHediffDef>.AllDefs)
                        {
                            if (scarName == def.typeName)
                            {
                                AvatarLayer scar = new (def.GetPath(gender, lifeStage), skinColor, headAttachmentOffset);
                                if (lifeStage != "") scar.offset = -1;
                                if (h.Part.def.defName == "Eye")
                                {
                                    if (h.Part.woundAnchorTag == "LeftEye")
                                        scar.drawDexter = false;
                                    else
                                        scar.drawSinister = false;
                                }
                                layers.Add(scar);
                            }
                        }
                    }
                }
                #if v1_4
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.graphicData.layer == GeneDrawLayer.PostTattoo)
                    {
                        layers.Add(AvatarLayer.FromGene(gene, pawn));
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                AvatarLayer beard = new (beardPath, hairColor, headAttachmentOffset);
                if (beardPath == "BEARD")
                    beard.fallback = new VanillaTexOption(pawn.style.beardDef.texPath + "_south", 8, RecolorOption.Yes);
                AvatarLayer hair = new (hairPath, hairColor, headAttachmentOffset);
                hair.hideTop = hairHideTop + headAttachmentOffset;
                if (hairPath == "HAIR")
                    hair.fallback = new VanillaTexOption(pawn.story.hairDef.texPath + "_south", 4, RecolorOption.Yes, true);
                // gradient hair mod support
                if (ModCompatibility.GradientHair_Loaded)
                {
                    if (ModCompatibility.GetGradientHair(pawn) is (String mask, Color color))
                    {
                        hair.gradientMask = mask;
                        hair.colorB = color;
                    }
                }
                AvatarLayer brows = new (browsPath, hairColor);
                if (!hideBeard && lifeStage != "Newborn")
                    layers.Add(beard);
                if (!hideEyes && lifeStage != "Newborn")
                    layers.Add(brows);
                if (!drawHeadgear)
                {
                    if (!hideHair && lifeStage != "Newborn")
                        layers.Add(hair);
                }
                else
                {
                    // facegear goes under hair
                    foreach ((Apparel apparel, AvatarFacegearDef def) in facegears)
                    {
                        layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset + (lifeStage == "" ? 0 : -1)));
                    }
                    // hair and headgear
                    if (!hideHair && lifeStage != "Newborn")
                        layers.Add(hair);
                    if (coversAll == null)
                        foreach ((Apparel apparel, AvatarHeadgearDef def) in headgears)
                        {
                            layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset));
                        }
                }
                if (!hideEars && (mod.settings.earsOnTop && ears.texPath != "Core/Unisex/Ears/Ears_Human")) layers.Add(ears);
                #if BIOTECH
                foreach (Gene gene in activeGenes)
                {
                    foreach (AvatarHeadboneDef def in DefDatabase<AvatarHeadboneDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            Color color = new (1,1,1,1);
                            switch (GeneUseColor(gene))
                            {
                                case 1: // hair
                                    color = hairColor;
                                    break;
                                case 2: // skin
                                    color = skinColor;
                                    break;
                            }
                            layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), color, headAttachmentOffset));
                        }
                }
                // dump all remaining cosmetic genes here
                foreach (Gene gene in cosmeticGenes)
                {
                    AvatarLayer layer = AvatarLayer.FromGene(gene, pawn);
                    layer.offset += headAttachmentOffset;
                    layers.Add(layer);
                }
                #endif
                if (drawHeadgear && coversAll != null)
                    layers.Add(coversAll);
            }
            // end of head drawing


            // render the texture
            foreach (AvatarLayer layer in layers)
            {
                if (layer.texPath != null)
                {
                    Texture2D texture = null;
                    Texture2D mask = null;
                    Texture2D alphaMask = null;
                    if (layer.fallback != null)
                    {
                        // fallback to vanilla texture
                        if (ContentFinder<Texture2D>.Get(layer.fallback.texPath, false) != null)
                            texture = TextureUtil.ProcessVanillaTexture(layer.fallback, (width, height), (62,68));
                    }
                    else
                    {
                        Texture2D unreadableTexture = mod.GetTexture(layer.texPath);
                        // the path is defined in the def so the texture should exist
                        if (unreadableTexture != null)
                            texture = TextureUtil.MakeReadableCopy(unreadableTexture);
                    }
                    if (texture != null)
                    {
                        string maskPath = string.IsNullOrEmpty(layer.maskPath) ? layer.texPath + "m" : layer.maskPath;
                        Texture2D maskTexture = mod.GetTexture(maskPath, false);
                        if (maskTexture != null)
                            mask = TextureUtil.MakeReadableCopy(maskTexture);
                        if (layer.alphaMaskPath != null)
                        {
                            Texture2D alphaMaskTexture = mod.GetTexture(layer.alphaMaskPath, false);
                            if (alphaMaskTexture != null)
                                alphaMask = TextureUtil.MakeReadableCopy(alphaMaskTexture);
                        }
                        if (mod.settings.avatarCompression)
                            texture.Compress(true);

                        // ad hoc stuff for gradient hair
                        if (!string.IsNullOrEmpty(layer.gradientMask))
                        {
                            VanillaTexOption opt = new (layer.gradientMask, 4, RecolorOption.No);
                            mask = TextureUtil.ProcessVanillaTexture(opt, (width, height), (62,68));
                        }

                        for (int y = Math.Max(height-texture.height-layer.offset, 0);
                            y < Math.Min(height-layer.hideTop-yOffset-layer.offset, height); y++)
                        {
                            for (int x = (layer.drawDexter ? 0 : width/2); x < (layer.drawSinister ? width : width/2); x++)
                            {
                                Color oldColor = downed ? canvas.GetPixel(y-halfWidthHeightDiff, x) : canvas.GetPixel(x, y);
                                Color newColor = texture.GetPixel(layer.flipGraphic ? (width-1 - x) : x, y-(height-texture.height-layer.offset)+yOffset);
                                float alpha = newColor.a;
                                if (alphaMask != null)
                                    alpha *= alphaMask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset).r;
                                if (alpha > 0)
                                {
                                    Color color = new ();
                                    if (layer.color is Color tint)
                                    {
                                        alpha *= tint.a;
                                        if (mask != null)
                                        {
                                            if (layer.colorB is Color tint2)
                                            {
                                                Color maskPixel = mask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset);
                                                float r = maskPixel.r;
                                                float g = maskPixel.g;
                                                color.r = oldColor.r*(1f-alpha) + newColor.r*(tint.r*r + tint2.r*g + (1-r)*(1-g))*alpha;
                                                color.g = oldColor.g*(1f-alpha) + newColor.g*(tint.g*r + tint2.g*g + (1-r)*(1-g))*alpha;
                                                color.b = oldColor.b*(1f-alpha) + newColor.b*(tint.b*r + tint2.b*g + (1-r)*(1-g))*alpha;
                                                color.a = 1f;
                                            }
                                            else
                                            {
                                                Color maskPixel = mask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset);
                                                float r = maskPixel.r;
                                                color.r = oldColor.r*(1f-alpha) + newColor.r*(tint.r*r + 1-r)*alpha;
                                                color.g = oldColor.g*(1f-alpha) + newColor.g*(tint.g*r + 1-r)*alpha;
                                                color.b = oldColor.b*(1f-alpha) + newColor.b*(tint.b*r + 1-r)*alpha;
                                                color.a = 1f;
                                            }
                                        }
                                        else
                                        {
                                            color.r = oldColor.r*(1f-alpha) + newColor.r*tint.r*alpha;
                                            color.g = oldColor.g*(1f-alpha) + newColor.g*tint.g*alpha;
                                            color.b = oldColor.b*(1f-alpha) + newColor.b*tint.b*alpha;
                                            color.a = 1f;
                                        }
                                    }
                                    else
                                    {
                                        color.r = oldColor.r*(1f-alpha) + newColor.r*alpha;
                                        color.g = oldColor.g*(1f-alpha) + newColor.g*alpha;
                                        color.b = oldColor.b*(1f-alpha) + newColor.b*alpha;
                                        color.a = 1f;
                                    }
                                    if (downed)
                                    {
                                        if (y >= halfWidthHeightDiff && y < height-halfWidthHeightDiff
                                            && x <= width-downedOffset)
                                            canvas.SetPixel(y-halfWidthHeightDiff, width-x-downedOffset, color);
                                    }
                                    else
                                        canvas.SetPixel(x, y, color);
                                }
                            }
                        }
                        if (layer.eyeColor is (Color, Color) eyeColor)
                        { // draw eye colors manually
                            foreach (EyePos eye in eyesPos)
                            {
                                if (downed)
                                {
                                    foreach (IntVec2 pos1 in eye.pos1)
                                        canvas.SetPixel(pos1.z+eyeLevel-halfWidthHeightDiff, width-pos1.x-downedOffset, eyeColor.Item1);
                                    foreach (IntVec2 pos2 in eye.pos2)
                                        canvas.SetPixel(pos2.z+eyeLevel-halfWidthHeightDiff, width-pos2.x-downedOffset, eyeColor.Item2);
                                }
                                else
                                {
                                    foreach (IntVec2 pos1 in eye.pos1)
                                        canvas.SetPixel(pos1.x, pos1.z+eyeLevel, eyeColor.Item1);
                                    foreach (IntVec2 pos2 in eye.pos2)
                                        canvas.SetPixel(pos2.x, pos2.z+eyeLevel, eyeColor.Item2);
                                }
                            }
                        }
                        if (alphaMask != null) UnityEngine.Object.Destroy(alphaMask);
                        if (mask != null) UnityEngine.Object.Destroy(mask);
                        UnityEngine.Object.Destroy(texture);
                    }
                }
            }
            if (pawn.Dead)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Color oldColor = canvas.GetPixel(x, y);
                        float gray = (oldColor.r + oldColor.g + oldColor.b-0.1f)/3f;
                        canvas.SetPixel(x, y, new Color(gray,gray,gray*1.2f,oldColor.a));
                    }
            canvas.Apply();
            if (avatar != null)
            { // destroy old texture
                UnityEngine.Object.Destroy(avatar);
            }
            if (mod.settings.avatarScaling)
                avatar = TextureUtil.ScaleX2(canvas);
            else
            {
                avatar = TextureUtil.MakeReadableCopy(canvas);
                avatar.Apply();
            }
            avatar.filterMode = FilterMode.Point;
            return avatar;
        }
        public virtual Texture2D GetAvatar(bool allowStatic = true)
        {
            if (allowStatic)
            {
                if (avatar == null || Time.frameCount > staticTextureLastCheck + 10) // don't check every frame
                    TryGetStaticTexture();
                if (staticTexture != null) return staticTexture;
            }
            if (updateQueued) ClearCachedAvatar();
            if (avatar == null || updateQueued)
            {
                PawnPortraitCategory cat = ClassifyPawn(pawn);
                if (cat == PawnPortraitCategory.Humanlike)
                    avatar = RenderAvatar();
                else
                    avatar = RenderAnimalAvatar();
            }
            return avatar;
        }
        // GetPawnNameStatic still exists for backward-compat in case any caller
        // needs the legacy display-name path. New code should use
        // GetPortraitFileBase(pawn) for storage and pawn.LabelShortCap for UI.
        public static string GetPawnNameStatic(Pawn pawn)
        {
            if (pawn == null || pawn.Name == null) return pawn?.thingIDNumber.ToString() ?? "unknown";
            string name = pawn.Name.ToStringFull.Replace("'", "").Replace(" ", "_");
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');
            return name + "_" + pawn.thingIDNumber.ToString();
        }

        // === Per-world rename-stable portrait file naming ===
        // Portraits live in <persistentDataPath>/avatar/ which is a USER-wide
        // directory shared across every save. Storing files as `<id>.png` (old
        // single-game scheme) caused cross-game contamination: thingIDNumber
        // resets to low values in each new game, so a new pawn with id=500
        // would inherit an OLD pawn id=500's portrait from a previous world.
        // Symptom users notice: "a new pawn spawned with the same name as one
        // from before and got that pawn's portrait".
        //
        // Current scheme: `<worldId>_<thingIDNumber>.png` where worldId is
        // `Find.World.info.persistentRandomValue` (long, stable per world,
        // randomly initialized on world creation). Two different worlds can't
        // collide. Two saves of the same world correctly share portraits.
        //
        // Migration handled in three places:
        //   1) AvatarGameComponent.LoadedGame() bulk-renames very old
        //      `<name>_<id>.png` â†’ `<id>.png` (only useful for users coming
        //      from a pre-id-naming version).
        //   2) GetPortraitPath() lazy per-pawn migration: copies legacy
        //      `<id>.png` to `<world>_<id>.png` on first lookup (COPY not move
        //      â€” the file might still belong to another world's pawn with the
        //      same id, and we can't know without scanning every world save).
        //   3) Same per-pawn fallback also looks for very old `<name>_<id>.png`
        //      and moves it (since those predate any world-aware storage).
        public static string GetPortraitFileBase(Pawn pawn)
        {
            if (pawn == null) return "unknown";
            long worldId = GetStableWorldId(pawn);
            if (worldId == 0L)
            {
                // Sidecar didn't exist and world unavailable; fall back to legacy
                // scheme. This is rare and temporary â€” world will be up next tick.
                return pawn.thingIDNumber.ToString();
            }
            return worldId.ToString() + "_" + pawn.thingIDNumber.ToString();
        }
        // Get the stable world ID for a pawn. This NEVER returns 0 â€” it will
        // cache the world ID in a sidecar file so even if Find.World becomes
        // null, we can still reliably load portraits from the same file.
        // The sidecar is per-pawn and lives in <persistentDataPath>/avatar/.
        // In-memory cache of resolved world IDs, keyed by pawn thingIDNumber.
        // A pawn's world ID is immutable for the session, but GetStableWorldId
        // is called from extremely hot paths (UIPatch.Postfix runs it several
        // times per frame for the selected pawn via GetPortraitPath +
        // TryGetStaticTexture; the 20s safety scan runs it per spawned pawn).
        // Without this cache every one of those calls did a File.Exists +
        // File.ReadAllText of the .worldid sidecar â€” ~100+ synchronous disk
        // reads/sec for a constant value. ConcurrentDictionary because the
        // background generation thread also resolves portrait paths.
        // We only cache positive resolutions (> 0); a 0 result means the
        // world wasn't available yet, which must stay re-resolvable.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> worldIdCache
            = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        private static long GetStableWorldId(Pawn pawn)
        {
            if (pawn == null) return 0L;
            int pawnId = pawn.thingIDNumber;
            // Fast path: resolved earlier this session.
            if (worldIdCache.TryGetValue(pawnId, out long memo))
                return memo;
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "avatar");
            string sidecarPath = System.IO.Path.Combine(dir, pawnId + ".worldid");
            
            // Try to read cached world ID from sidecar
            try
            {
                if (System.IO.File.Exists(sidecarPath))
                {
                    string text = System.IO.File.ReadAllText(sidecarPath).Trim();
                    if (long.TryParse(text, out long cached))
                    {
                        worldIdCache[pawnId] = cached;
                        return cached;
                    }
                }
            }
            catch { }
            
            // Sidecar missing or invalid â€” try to get the live world ID
            long worldId = TryGetWorldId();
            if (worldId > 0L)
            {
                // Write it to the sidecar for next time
                try
                {
                    if (!System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(sidecarPath, worldId.ToString());
                }
                catch { }
                worldIdCache[pawnId] = worldId;
                return worldId;
            }
            
            // World unavailable and no cached sidecar â€” fall back to legacy <id>.png
            // This is temporary and will be fixed once world is up.
            return 0L;
        }
        // Defensive fetch â€” Find.World can be null during early load, between
        // saves, or during world generation. We return 0 as the sentinel; the
        // caller treats that as "world unavailable".
        private static long TryGetWorldId()
        {
            try { return Find.World?.info?.persistentRandomValue ?? 0L; }
            catch { return 0L; }
        }
        // Style-scoped portrait directory. RimWorld style keeps the historical
        // root (<persistentDataPath>/avatar) so existing portraits keep working
        // and nothing has to migrate; Ultra-realistic portraits live in a
        // sibling "realistic" subfolder. Every read / write / existence-check
        // routes through here, so switching styles makes the "missing portrait"
        // checks look in the OTHER folder â€” the previous style's images are never
        // shown, and the auto-generator repaints into the active folder on the
        // next safety scan / inspect-pane click.
        //
        // Deliberately NOT style-scoped (shared root): the ComfyUI install dir,
        // the managed-PID sidecar, piptmp, the per-pawn .worldid sidecars, and
        // the bulk legacy migration â€” all of those are style-independent.
        public static string GetPortraitDir()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, "avatar");
        }
        private static bool CurrentStyleIsRimWorld()
        {
            return true;
        }
        public static string GetPortraitPath(Pawn pawn)
        {
            string dir = GetPortraitDir();
            string newPath = System.IO.Path.Combine(dir, GetPortraitFileBase(pawn) + ".png");
            // Legacy on-disk filename formats only ever existed in the historical
            // root dir (RimWorld style). The realistic subfolder is new, so skip
            // migration there â€” and skip MARKING this pawn migration-checked,
            // which would otherwise block a later RimWorld-style lookup from
            // migrating its real legacy file (legacyMigrationChecked is keyed by
            // pawn id, not by directory).
            if (CurrentStyleIsRimWorld())
                MigrateLegacyPortraitForPawn(pawn, dir, newPath);
            return newPath;
        }
        // Lazy per-pawn migration. Two formats to handle:
        //   (a) <id>.png â€” previous single-game scheme. COPY to <world>_<id>.png
        //       so the same source file can serve multiple worlds without each
        //       world stealing it from the others on lookup.
        //   (b) <name>_<id>.png â€” very old scheme from pre-1.0 of this mod.
        //       Safe to MOVE; nothing else points at these files.
        // Bounded to one scan per pawn per session via a static set.
        private static readonly HashSet<int> legacyMigrationChecked = new HashSet<int>();
        // Match files in the world-namespaced format: <digits>_<digits>.png.
        // We use this to EXCLUDE our own new-format files from the legacy
        // `*_<id>.png` glob â€” otherwise we'd accidentally move our own
        // freshly-written portraits during migration.
        private static readonly System.Text.RegularExpressions.Regex WorldNamespacedFile =
            new System.Text.RegularExpressions.Regex(@"^\d+_\d+\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static void MigrateLegacyPortraitForPawn(Pawn pawn, string dir, string newPath)
        {
            if (pawn == null) return;
            int id = pawn.thingIDNumber;
            if (legacyMigrationChecked.Contains(id)) return;
            legacyMigrationChecked.Add(id);
            try
            {
                if (System.IO.File.Exists(newPath)) return;
                if (!System.IO.Directory.Exists(dir)) return;
                // (a) Single-game legacy: <id>.png. COPY to the world-namespaced
                // path so other worlds' lookups still find this file too.
                string legacyIdPath = System.IO.Path.Combine(dir, id + ".png");
                if (System.IO.File.Exists(legacyIdPath))
                {
                    try
                    {
                        System.IO.File.Copy(legacyIdPath, newPath);
                        Log.Message("Avatar: migrated single-game portrait " + id + ".png â†’ " + System.IO.Path.GetFileName(newPath) + " for " + pawn.LabelShortCap);
                        return;
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Avatar: <id>.png â†’ <world>_<id>.png copy failed: " + e.Message);
                    }
                }
                // (b) Very old legacy: <anything>_<id>.png. MOVE â€” these predate
                // world-aware storage. Filter out our OWN <digits>_<digits>.png
                // format which also matches the *_<id>.png glob.
                string suffix = "_" + id + ".png";
                string[] candidates = System.IO.Directory.GetFiles(dir, "*" + suffix);
                foreach (string legacy in candidates)
                {
                    string filename = System.IO.Path.GetFileName(legacy);
                    if (WorldNamespacedFile.IsMatch(filename)) continue;
                    try
                    {
                        System.IO.File.Move(legacy, newPath);
                        Log.Message("Avatar: migrated legacy portrait " + filename + " â†’ " + System.IO.Path.GetFileName(newPath));
                        return;
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Avatar: legacy portrait migration failed for " + legacy + ": " + e.Message);
                    }
                }
            }
            catch (Exception e) { Log.Warning("Avatar: legacy migration scan failed: " + e.Message); }
        }
        // One-shot bulk migration. Called from AvatarGameComponent.LoadedGame.
        // Renames very-old `<name>_<id>.png` files to `<id>.png` (the lazy
        // per-pawn migration then copies those to `<world>_<id>.png` on first
        // lookup). Conflicts (both old and new exist) prefer the newer file
        // by mtime.
        //
        // Skips our world-namespaced `<digits>_<digits>.png` format â€” those
        // ALSO match the `_<digits>.png` glob, and stripping the world prefix
        // would re-introduce the cross-game contamination this exists to prevent.
        public static void BulkMigrateLegacyPortraits()
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "avatar");
                if (!System.IO.Directory.Exists(dir)) return;
                System.Text.RegularExpressions.Regex re =
                    new System.Text.RegularExpressions.Regex(@"_(\d+)\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int migrated = 0;
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.png"))
                {
                    string name = System.IO.Path.GetFileName(file);
                    // Skip files already in single-game `<digits>.png` format.
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        continue;
                    // Skip world-namespaced `<digits>_<digits>.png` â€” these are
                    // the new format we're trying to MIGRATE TO, not from.
                    if (WorldNamespacedFile.IsMatch(name))
                        continue;
                    var m = re.Match(name);
                    if (!m.Success) continue;
                    string id = m.Groups[1].Value;
                    string target = System.IO.Path.Combine(dir, id + ".png");
                    try
                    {
                        if (System.IO.File.Exists(target))
                        {
                            // Both exist â€” keep the more recently modified one.
                            DateTime oldMtime = System.IO.File.GetLastWriteTimeUtc(file);
                            DateTime newMtime = System.IO.File.GetLastWriteTimeUtc(target);
                            if (oldMtime > newMtime)
                            {
                                System.IO.File.Delete(target);
                                System.IO.File.Move(file, target);
                                migrated++;
                            }
                            else
                            {
                                System.IO.File.Delete(file); // legacy is stale; drop it
                            }
                        }
                        else
                        {
                            System.IO.File.Move(file, target);
                            migrated++;
                        }
                    }
                    catch (Exception e) { Log.Warning("Avatar: bulk migrate failed for " + name + ": " + e.Message); }
                }
                if (migrated > 0)
                    Log.Message("Avatar: bulk-migrated " + migrated + " legacy portrait file(s) to the new <id>.png scheme.");
            }
            catch (Exception e) { Log.Warning("Avatar: BulkMigrateLegacyPortraits threw: " + e.Message); }
        }

        public string GetPawnName()
        {
            return GetPawnNameStatic(pawn);
        }
        public void TryGetStaticTexture()
        {
            staticTextureLastCheck = Time.frameCount;
            string path = GetPortraitPath(pawn);
            if (System.IO.File.Exists(path))
            {
                if (staticTexture == null)
                    staticTexture = new (1, 1);
                DateTime lastModified = System.IO.File.GetLastWriteTime(path);
                if (lastModified != staticTextureLastModified)
                {
                    try
                    {
                        staticTexture.LoadImage(System.IO.File.ReadAllBytes(path));
                        staticTextureLastModified = lastModified;
                    }
                    catch (System.IO.IOException)
                    {
                        // read failed: probably the image is still being written
                    }
                }
            }
            else if (staticTexture != null)
            {
                staticTextureLastModified = null;
                UnityEngine.Object.Destroy(staticTexture);
                staticTexture = null;
            }
        }
        private static void SavePng(string filename, Texture2D texture)
        {
            string dir = GetPortraitDir();
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            TextureUtil.SavePng(System.IO.Path.Combine(dir, filename), texture);
        }
        public void SaveAsPng()
        {
            SavePng("avatar-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".png", avatar);
        }
        public void UpscaleSaveAsPng()
        {
            Texture2D upscaled = TextureUtil.MakeReadableCopy(avatar, 480, 576);
            SavePng("avatar-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + "-upscaled.png", upscaled);
            UnityEngine.Object.Destroy(upscaled);
        }
        public void EnableStatic()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            string backup = System.IO.Path.Combine(dir, fileBase + "-backup.png");
            if (System.IO.File.Exists(backup))
            {
                System.IO.File.Move(backup, path);
            }
            else
            {
                Texture2D upscaled = TextureUtil.MakeReadableCopy(avatar, 480, 576);
                SavePng(fileBase + ".png", upscaled);
                UnityEngine.Object.Destroy(upscaled);
            }
            TryGetStaticTexture();
        }
        public void DisableStatic()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            if (System.IO.File.Exists(path))
            {
                string backup = System.IO.Path.Combine(dir, fileBase + "-backup.png");
                if (System.IO.File.Exists(backup))
                    System.IO.File.Delete(backup);
                System.IO.File.Move(path, backup);
            }
        }
        public string GetPrompts()
        {
            // Resolve o template base
            string result = mod.settings.aiGenPreamble
                .Replace("{age}", pawn.ageTracker.AgeBiologicalYears.ToString())
                .Replace("{gender}", (pawn.gender == Gender.Female) ? "female" : "male")
                .Replace("{race}", GetRaceLabel())
                .Replace("{lifestage}", GetLifeStageLabel())
                .Replace("{bodytype}", GetBodyTypeLabel())
                .Replace("{skincolor}", GetSkinColorLabel())
                .Replace("{haircolor}", GetHairColorLabel())
                .Replace("{hair}", GetHairLabel())
                .Replace("{beard}", GetBeardLabel())
                .Replace("{apparel}", GetApparelLabel())
                .Replace("{items}", GetItemsLabel())
                .Replace("{mood}", GetMoodLabel())
                .Replace("{personality}", GetPersonalityLabel())
                .Replace("{traits}", GetTraitsLabel())
                .Replace("{health}", GetHealthLabel())
                .Replace("{implants}", GetImplantsLabel())
                .Replace("{prosthetics}", GetProstheticsLabel());
            
            // Append art style as a clear directive
            string artSuffix = AvatarMod.GetArtStylePrompt(mod.settings.artStyle, mod.settings.customStylePrompt);
            if (!string.IsNullOrEmpty(artSuffix))
                result += ". Art style: " + artSuffix + ".";
            result += " Portrait orientation, vertical composition. Plain white background, studio lighting.";
            
            // Clean up double commas/spaces/dots
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*,\s*,+\s*", ", ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\.\s*\.,", ".,");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s{2,}", " ");
            
            return result.Trim();
        }
        
        private string GetRaceLabel()
        {
            if (pawn?.RaceProps?.AnyPawnKind?.race != null)
            {
                string defName = pawn.RaceProps.AnyPawnKind.race.defName;
                if (defName != "Human")
                    return pawn.RaceProps.AnyPawnKind.race.label?.ToLower() ?? defName;
            }
            return XenotypeDescriptionGenerator.GetRaceDescription(pawn);
        }
        
        private string GetLifeStageLabel()
        {
            string stage = pawn?.ageTracker?.CurLifeStage?.defName ?? "adult";
            if (stage.StartsWith("Humanlike")) stage = stage.Substring(10);
            return stage.ToLower();
        }
        
        private string GetBodyTypeLabel()
        {
            return pawn?.story?.bodyType?.defName?.ToLower() ?? "average";
        }
        
        private string GetSkinColorLabel()
        {
            return DescribeColor(pawn?.story?.SkinColor);
        }
        
        private string GetHairColorLabel()
        {
            return DescribeColor(pawn?.story?.hairColor);
        }
        
        private string DescribeColor(Color? color)
        {
            if (color == null) return "natural";
            float r = color.Value.r, g = color.Value.g, b = color.Value.b;
            if (r > 0.8f && g > 0.7f && b > 0.6f) return "fair";
            if (r > 0.7f && g > 0.5f && b > 0.4f) return "light brown";
            if (r > 0.5f && g > 0.4f && b > 0.3f) return "brown";
            if (r > 0.3f && g > 0.3f && b > 0.3f) return "dark brown";
            if (r > 0.8f && g > 0.8f && b > 0.7f) return "pale";
            if (r > 0.7f && g > 0.6f && b > 0.5f) return "tan";
            if (r < 0.4f && g < 0.4f && b > 0.5f) return "dark";
            return "natural";
        }
        
        private string GetHairLabel()
        {
            string hair = pawn?.story?.hairDef?.label;
            if (string.IsNullOrEmpty(hair) || hair == "Bald") return "bald, no hair";
            return hair.ToLower() + " hair";
        }
        
        private string GetBeardLabel()
        {
            if (pawn?.style?.beardDef == null) return "no beard";
            string beard = pawn.style.beardDef.label;
            if (beard == "No beard") return "clean shaven, no beard";
            return beard.ToLower() + " beard";
        }
        
        private string GetApparelLabel()
        {
            if (!drawClothes || pawn?.apparel?.WornApparel == null) return "no visible clothing";
            var items = new List<string>();
            foreach (Apparel a in pawn.apparel.WornApparel)
            {
                if (a.def?.apparel?.layers?.Exists(p => p.defName == "Overhead" || p.defName == "EyeCover") == true && !drawHeadgear)
                    continue;
                string label = a.def?.label;
                if (!string.IsNullOrEmpty(label) && !items.Contains(label))
                    items.Add(label.ToLower());
            }
            if (items.Count == 0) return "no visible clothing";
            return string.Join(", ", items.Take(4));
        }
        
        private string GetItemsLabel()
        {
            try
            {
                var items = new List<string>();
                if (pawn?.equipment?.Primary?.def?.label != null)
                    items.Add(pawn.equipment.Primary.def.label.ToLower());
                return items.Count > 0 ? "Holding " + string.Join(", ", items.Take(3)) : "no items visible";
            }
            catch { return "no items visible"; }
        }
        
        private string GetMoodLabel()
        {
            try
            {
                if (pawn?.needs?.mood == null) return "";
                float mood = pawn.needs.mood.CurLevelPercentage;
                if (mood > 0.8f) return "happy expression, content";
                if (mood > 0.6f) return "neutral expression";
                if (mood > 0.4f) return "slightly unhappy expression";
                if (mood > 0.2f) return "unhappy expression, stressed";
                return "very distressed expression, miserable";
            }
            catch { return ""; }
        }
        
        private string GetPersonalityLabel()
        {
            try
            {
                var traits = pawn?.story?.traits?.allTraits;
                if (traits == null || traits.Count == 0) return "";
                foreach (var t in traits)
                {
                    if (t?.def == null) continue;
                    if (t.Degree > 1) return t.def.label.ToLower() + " personality";
                }
                if (traits.Count > 0 && traits[0]?.def != null)
                    return traits[0].def.label.ToLower() + " personality";
            }
            catch { }
            return "";
        }
        
        private string GetTraitsLabel()
        {
            try
            {
                var traits = pawn?.story?.traits?.allTraits;
                if (traits == null || traits.Count == 0) return "none notable";
                return string.Join(", ", traits.Where(t => t?.def != null).Select(t => t.def.label.ToLower()).Take(3));
            }
            catch { return "none notable"; }
        }
        
        private string GetHealthLabel()
        {
            try
            {
                if (pawn?.health?.hediffSet == null) return "healthy";
                var visible = new List<string>();
                foreach (var h in pawn.health.hediffSet.hediffs)
                {
                    if (h?.Visible == true && h?.def?.label != null)
                    {
                        string label = h.def.label.ToLower();
                        if (!visible.Contains(label) && !label.Contains("missing"))
                            visible.Add(label);
                    }
                }
                if (visible.Count == 0) return "healthy";
                return "has " + string.Join(", ", visible.Take(3));
            }
            catch { return "healthy"; }
        }
        
        private string GetImplantsLabel()
        {
            try
            {
                if (pawn?.health?.hediffSet == null) return "no cybernetic implants";
                var implants = new List<string>();
                foreach (var h in pawn.health.hediffSet.hediffs)
                {
                    if ((h is Hediff_AddedPart || h is Hediff_Implant) && h?.def?.label != null)
                    {
                        string label = h.def.label.ToLower();
                        if (!implants.Contains(label))
                            implants.Add(label);
                    }
                }
                if (implants.Count == 0) return "no cybernetic implants";
                return "Cybernetic implants: " + string.Join(", ", implants.Take(3));
            }
            catch { return "no cybernetic implants"; }
        }
        
        private string GetProstheticsLabel()
        {
            try
            {
                if (pawn?.health?.hediffSet == null) return "no visible prosthetics";
                var prosthetics = new List<string>();
                foreach (var h in pawn.health.hediffSet.hediffs)
                {
                    if (h is Hediff_AddedPart && h?.Part?.def?.label != null)
                    {
                        string part = h.Part.def.label.ToLower();
                        if (!prosthetics.Contains(part))
                            prosthetics.Add(part);
                    }
                }
                if (prosthetics.Count == 0) return "no visible prosthetics";
                return "Prosthetic " + string.Join(", ", prosthetics.Take(2));
            }
            catch { return "no visible prosthetics"; }
        }
        public void OpenPromptsWindow()
        {
            Find.WindowStack.Add(new Prompts_Window(pawn, drawHeadgear, drawClothes));
        }

        public void GeneratePortraitImmediate()
        {
            if (pawn == null) return;
            bool isCreature = !pawn.RaceProps.Humanlike;
            string prompts = isCreature ? GetCreaturePrompts() : GetPrompts();
            if (string.IsNullOrEmpty(prompts))
            {
                Messages.Message("Prompts cannot be empty", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            string imagePath = SaveToStaticPortrait();
            string outputPath = imagePath;
            int pawnId = pawn.thingIDNumber;
            string pawnLabel = pawn.LabelShortCap;
            DateTime startedUtc = DateTime.UtcNow;

            AvatarMod.MarkPending(pawnId);
            AvatarMod.ClearFailedAttempts(pawnId);

            ApiClient.GeneratePortraitAsync(imagePath, prompts, outputPath, (success, error) =>
            {
                if (success)
                {
                    TextureUtil.RemoveBackground(outputPath);
                    double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                    AvatarMod.MarkAutoGen(pawnId);
                }
                else
                {
                    AvatarMod.RecordFailedAttempt(pawnId);
                    AvatarMod.UnmarkAutoGen(pawnId);
                    Messages.Message("Portrait generation failed: " + (error ?? "unknown error"), MessageTypeDefOf.RejectInput, historical: false);
                }
                AvatarMod.UnmarkPending(pawnId);
            }, startedUtc, isCreature: isCreature);

            Messages.Message("AI portrait generation started (API)", MessageTypeDefOf.TaskCompletion, historical: false);
        }

        public void GeneratePortraitImmediateSilent()
        {
            bool isCreature = !pawn.RaceProps.Humanlike;
            string prompts = isCreature ? GetCreaturePrompts() : GetPrompts();
            if (string.IsNullOrEmpty(prompts)) return;
            string imagePath = SaveToStaticPortrait();
            string outputPath = imagePath;
            int pawnId = pawn.thingIDNumber;
            string pawnLabel = pawn.LabelShortCap;
            DateTime startedUtc = DateTime.UtcNow;

            AvatarMod.MarkPending(pawnId);

            ApiClient.GeneratePortraitAsync(imagePath, prompts, outputPath, (success, error) =>
            {
                if (success)
                {
                    TextureUtil.RemoveBackground(outputPath);
                    double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                    AvatarMod.MarkAutoGen(pawnId);
                }
                else
                {
                    AvatarMod.RecordFailedAttempt(pawnId);
                    AvatarMod.UnmarkAutoGen(pawnId);
                }
                AvatarMod.UnmarkPending(pawnId);
            }, startedUtc, isCreature: isCreature);
        }
        public string SaveToStaticPortrait()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            Texture2D upscaled = TextureUtil.MakeReadableCopy(GetAvatar(false), 480, 576);
            SavePng(fileBase + ".png", upscaled);
            UnityEngine.Object.Destroy(upscaled);
            ClearCachedAvatar();
            return path;
        }
        public void ToggleAvatarVisibility()
        {
            if (AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber))
            {
                AvatarMod.hiddenPawns.Remove(pawn.thingIDNumber);
                Messages.Message("Avatar shown", MessageTypeDefOf.TaskCompletion, historical: false);
            }
            else
            {
                AvatarMod.hiddenPawns.Add(pawn.thingIDNumber);
                Messages.Message("Avatar hidden", MessageTypeDefOf.TaskCompletion, historical: false);
            }
        }

        public FloatMenu GetFloatMenu()
        {
            if (pawn == null) return new FloatMenu(new List<FloatMenuOption>());
            List<FloatMenuOption> options = new ();
            bool isHidden = AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber);
            if (staticTexture == null)
            {
                options.Add(new ("Generate portrait", GeneratePortraitImmediate));
            }
            else
            {
                options.Add(new ("Regenerate portrait", GeneratePortraitImmediate));
            }
            // Manual prompt-editor variant: opens Prompts_Window so the user
            // can tweak the auto-generated prompt before kicking off the run.
            options.Add(new ("Regenerate portrait (Adjust prompt)", OpenPromptsWindow));
            options.Add(new (isHidden ? "Show this image" : "Hide this image", ToggleAvatarVisibility));
            return new FloatMenu(options);
        }
        public bool CheckCursor(Vector2 pos)
        {
            Texture2D displayed = GetAvatar();
            if (displayed == null) return false;
            int x = (int) (pos.x * (float) displayed.width);
            int y = (int) (pos.y * (float) displayed.height);
            return displayed.GetPixel(x, y).a > 0;
        }
        // ================================================================
        // CREATURE CLASSIFICATION SYSTEM
        // ================================================================
        
        public static PawnPortraitCategory ClassifyPawn(Pawn pawn)
        {
            if (pawn == null) return PawnPortraitCategory.Other;
            
            // CAMADA 1: API padrão RimWorld
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
            
            // CAMADA 2: CreaturePromptDef XML
            if (pawn.kindDef != null)
            {
                var def = ModCreatureRegistry.GetDefForKind(pawn.kindDef.defName);
                if (def != null && def.category != PawnPortraitCategory.Animal && def.category != PawnPortraitCategory.Other)
                    return def.category;
            }
            
            // CAMADA 3+4: Keywords ou fallback
            return InferCategoryFromKeywords(pawn);
        }
        
        private static PawnPortraitCategory InferCategoryFromKeywords(Pawn pawn)
        {
            string profile = BuildPawnProfile(pawn);
            
            if (MatchesAny(profile, "dragon", "wyvern", "drake", "wyrm", "dragonkin", "draconic"))
                return PawnPortraitCategory.Dragon;
            if (MatchesAny(profile, "zombie", "skeleton", "undead", "lich", "wraith", "ghost", "specter", "spectre", "banshee", "revenant", "wight", "ghoul"))
                return PawnPortraitCategory.Undead;
            if (MatchesAny(profile, "demon", "devil", "fiend", "imp", "succubus", "incubus", "hellspawn", "daemon", "infernal", "hellhound"))
                return PawnPortraitCategory.Demon;
            if (MatchesAny(profile, "angel", "celestial", "divine", "god", "titan", "seraph", "cherub", "archon_", "holy", "demigod"))
                return PawnPortraitCategory.Celestial;
            if (MatchesAny(profile, "elemental", "fire_element", "water_element", "earth_element", "air_element", "ice_element", "ifrit", "sylph", "gnome", "undine"))
                return PawnPortraitCategory.Elemental;
            if (MatchesAny(profile, "golem", "construct", "clockwork", "animated_armor", "living_statue", "warforged"))
                return PawnPortraitCategory.Construct;
            if (MatchesAny(profile, "eldritch", "cthulhu", "lovecraft", "shoggoth", "cosmic_horror", "void_", "fleshbeast", "deep_one", "star_spawn"))
                return PawnPortraitCategory.Aberration;
            if (MatchesAny(profile, "mutant", "mutated", "abomination", "chimera", "irradiated"))
                return PawnPortraitCategory.Mutant;
            if (MatchesAny(profile, "treant", "mandrake", "myconid", "fungoid", "carnivorous_plant"))
                return PawnPortraitCategory.Plant;
            if (MatchesAny(profile, "slime", "ooze", "gelatinous", "jelly", "blob", "plasmoid", "amorph"))
                return PawnPortraitCategory.Slime;
            if (MatchesAny(profile, "fish", "shark", "whale", "kraken", "leviathan", "squid", "octopus", "merfolk", "mermaid", "nautilus", "aquatic", "marine"))
                return PawnPortraitCategory.Aquatic;
            
            if (pawn.RaceProps.Animal)
                return PawnPortraitCategory.Animal;
            return PawnPortraitCategory.Other;
        }
        
        private static string BuildPawnProfile(Pawn pawn)
        {
            if (pawn == null) return "";
            var sb = new System.Text.StringBuilder();
            if (pawn.kindDef != null)
            {
                sb.AppendLine(pawn.kindDef.defName.ToLowerInvariant());
                sb.AppendLine((pawn.kindDef.label ?? "").ToLowerInvariant());
            }
            if (pawn.RaceProps?.AnyPawnKind?.race != null)
            {
                sb.AppendLine(pawn.RaceProps.AnyPawnKind.race.defName.ToLowerInvariant());
                sb.AppendLine((pawn.RaceProps.AnyPawnKind.race.label ?? "").ToLowerInvariant());
            }
            if (pawn.RaceProps?.body != null)
            {
                sb.AppendLine(pawn.RaceProps.body.defName.ToLowerInvariant());
            }
            return sb.ToString();
        }
        
        private static bool MatchesAny(string profile, params string[] keywords)
        {
            foreach (string kw in keywords)
                if (profile.Contains(kw))
                    return true;
            return false;
        }
        private Texture2D RenderAnimalAvatar()
        {
            int width = 80;
            int height = 80;
            if (canvas == null)
                canvas = new Texture2D(width, height);
            
            // Fundo sempre visível para garantir área clicável
            Color fallbackColor = (pawn.Faction != null)
                ? new Color(pawn.Faction.Color.r * 0.3f, pawn.Faction.Color.g * 0.3f, pawn.Faction.Color.b * 0.3f, 0.5f)
                : new Color(0.3f, 0.3f, 0.3f, 0.5f);
            TextureUtil.ClearTexture(canvas, fallbackColor);
            
            bool rendered = false;
            try
            {
                float bodyFactor = (pawn.ageTracker?.CurLifeStage?.bodySizeFactor ?? 1f)
                    * (pawn.RaceProps?.baseBodySize ?? 1f);
                Vector2 portraitSize = new Vector2(
                    Mathf.Clamp(bodyFactor * 30f, 40f, width),
                    Mathf.Clamp(bodyFactor * 36f, 40f, height));
                
                RenderTexture portraitRT = PortraitsCache.Get(
                    pawn, portraitSize, Rot4.South,
                    default(Vector3), 1f, true, true,
                    false, false);
                
                if (portraitRT != null)
                {
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = portraitRT;
                    
                    int px = Mathf.Max(0, (width - (int)portraitSize.x) / 2);
                    int py = Mathf.Max(0, (height - (int)portraitSize.y) / 2);
                    int pw = Mathf.Min((int)portraitSize.x, width - px);
                    int ph = Mathf.Min((int)portraitSize.y, height - py);
                    
                    canvas.ReadPixels(new Rect(0, 0, pw, ph), px, py);
                    canvas.Apply();
                    rendered = true;
                    
                    RenderTexture.active = previous;
                }
            }
            catch (System.Exception) { }
            
            // Se o render vanilla falhou, garante um retângulo opaco e visível
            if (!rendered)
            {
                Color solidBg = (pawn.Faction != null)
                    ? new Color(pawn.Faction.Color.r, pawn.Faction.Color.g, pawn.Faction.Color.b, 0.8f)
                    : new Color(0.4f, 0.4f, 0.5f, 0.8f);
                TextureUtil.ClearTexture(canvas, solidBg);
            }
            
            return canvas;
        }
        public string GetCreaturePrompts()
        {
            PawnPortraitCategory category = ClassifyPawn(pawn);
            PawnKindDef kind = pawn.kindDef;
            
            string specificPrompt = (kind != null) ? ModCreatureRegistry.GetPromptForKind(kind.defName) : null;
            
            // Build description: use XML prompt if available, else category base + traits
            string description;
            if (!string.IsNullOrEmpty(specificPrompt))
            {
                description = specificPrompt;
            }
            else
            {
                string catDesc = GetCategoryBasePrompt(category);
                string traits = DetectCreatureTraits();
                description = string.IsNullOrEmpty(catDesc) ? traits : catDesc;
                if (!string.IsNullOrEmpty(traits) && !string.IsNullOrEmpty(catDesc))
                    description = catDesc + ", " + traits;
            }
            
            string speciesName = (kind?.label ?? kind?.defName ?? "creature").ToLower();
            
            // Select category-appropriate template
            string template;
            switch (category)
            {
                case PawnPortraitCategory.Mechanoid:
                    template = mod.settings.aiGenMechPreamble;
                    break;
                case PawnPortraitCategory.Insect:
                    template = mod.settings.aiGenInsectPreamble;
                    break;
                case PawnPortraitCategory.Dragon:
                    template = mod.settings.aiGenDragonPreamble;
                    break;
                case PawnPortraitCategory.Aquatic:
                    template = mod.settings.aiGenAquaticPreamble;
                    break;
                case PawnPortraitCategory.Plant:
                case PawnPortraitCategory.Dryad:
                    template = mod.settings.aiGenPlantPreamble;
                    break;
                case PawnPortraitCategory.AnomalyEntity:
                case PawnPortraitCategory.Undead:
                case PawnPortraitCategory.Demon:
                case PawnPortraitCategory.Celestial:
                case PawnPortraitCategory.Elemental:
                case PawnPortraitCategory.Construct:
                case PawnPortraitCategory.Slime:
                case PawnPortraitCategory.Aberration:
                case PawnPortraitCategory.Mutant:
                    template = mod.settings.aiGenEntityPreamble;
                    break;
                case PawnPortraitCategory.Other:
                    template = mod.settings.aiGenOtherPreamble;
                    break;
                default: // Animal
                    template = mod.settings.aiGenAnimalPreamble;
                    break;
            }
            
            // Resolve template
            string result = template
                .Replace("{age}", GetAnimalAgeDescription())
                .Replace("{gender}", GetGenderDescription())
                .Replace("{race}", speciesName)
                .Replace("{lifestage}", GetLifeStageLabel())
                .Replace("{size}", GetSizeAdjective())
                .Replace("{description}", description)
                .Replace("{health}", GetHealthLabel());
            
            // Append art style as a clear directive
            string artSuffix = AvatarMod.GetArtStylePrompt(mod.settings.artStyle, mod.settings.customStylePrompt);
            if (!string.IsNullOrEmpty(artSuffix))
                result += ". Art style: " + artSuffix + ".";
            result += " Portrait orientation, vertical composition. Plain white background, studio lighting.";
            
            // Clean up
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*,\s*,+\s*", ", ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s{2,}", " ");
            result = result.Replace("-year-old  ", "-year-old ");
            result = result.Replace(". .", ". ");
            
            return result.Trim();
        }
        
        private string GetCategoryBasePrompt(PawnPortraitCategory category)
        {
            switch (category)
            {
                case PawnPortraitCategory.AnomalyEntity:
                    return "eldritch horror, unnatural anatomy, cosmic dread, dark atmosphere, bioluminescent, otherworldly";
                case PawnPortraitCategory.Mechanoid:
                    return "mechanical, metal surface, robotic, industrial design, matte metal finish, sci-fi, detailed machinery";
                case PawnPortraitCategory.Insect:
                    return "exoskeleton, chitin, insectoid, detailed carapace, compound eyes, mandibles, arthropod";
                case PawnPortraitCategory.Dryad:
                    return "plant-like creature, organic, bark texture, symbiotic, nature spirit, glowing";
                case PawnPortraitCategory.Dragon:
                    return "dragon, massive scaled wings, reptilian, ancient, powerful, majestic";
                case PawnPortraitCategory.Undead:
                    return "undead, decaying flesh, skeletal, supernatural, dark atmosphere";
                case PawnPortraitCategory.Demon:
                    return "demonic, infernal, hellish, horns, dark red aura, sinister";
                case PawnPortraitCategory.Celestial:
                    return "divine, radiant, holy light, angelic wings, golden aura, majestic";
                case PawnPortraitCategory.Elemental:
                    return "elemental energy, raw power, swirling essence, magical";
                case PawnPortraitCategory.Construct:
                    return "artificial construct, stone or metal body, animated, runic markings";
                case PawnPortraitCategory.Slime:
                    return "gelatinous, amorphous, translucent, oozing, blob-like";
                case PawnPortraitCategory.Aberration:
                    return "eldritch abomination, cosmic horror, impossible anatomy, lovecraftian";
                case PawnPortraitCategory.Mutant:
                    return "mutated, twisted flesh, genetic horror, asymmetrical, grotesque";
                case PawnPortraitCategory.Plant:
                    return "plant creature, bark and leaves, rooted, natural, forest spirit";
                case PawnPortraitCategory.Aquatic:
                    return "aquatic, marine creature, scales, fins, underwater, deep sea";
                default: return "";
            }
        }
        
        private string DetectCreatureTraits()
        {
            var traits = new List<string>();
            try
            {
                float bodySize = (pawn.ageTracker?.CurLifeStage?.bodySizeFactor ?? 1f)
                    * (pawn.RaceProps?.baseBodySize ?? 1f);
                if (bodySize >= 3.0) traits.Add("colossal size, giant creature");
                else if (bodySize >= 2.0) traits.Add("large size");
                else if (bodySize <= 0.4) traits.Add("tiny, diminutive");
                if (pawn.RaceProps?.predator == true) traits.Add("predator, sharp teeth, hunter");
            }
            catch { }
            return traits.Count > 0 ? string.Join(", ", traits) : "";
        }
        
        private string GetAnimalAgeDescription()
        {
            if (pawn?.ageTracker?.CurLifeStage == null) return "adult";
            string stage = pawn.ageTracker.CurLifeStage.defName;
            if (stage.Contains("Baby") || stage.Contains("Newborn")) return "baby";
            if (stage.Contains("Juvenile")) return "juvenile";
            return "adult";
        }
        
        private string GetSizeDescription()
        {
            try
            {
                float size = (pawn.ageTracker?.CurLifeStage?.bodySizeFactor ?? 1f)
                    * (pawn.RaceProps?.baseBodySize ?? 1f);
                if (size >= 3.0) return "massive, towering";
                if (size >= 2.0) return "large";
                if (size >= 1.2) return "medium-sized";
                if (size <= 0.4) return "tiny, miniature";
                return "medium";
            }
            catch { return "medium"; }
        }
        
        private string GetSizeAdjective()
        {
            try
            {
                float size = (pawn.ageTracker?.CurLifeStage?.bodySizeFactor ?? 1f)
                    * (pawn.RaceProps?.baseBodySize ?? 1f);
                if (size >= 3.0) return "massive";
                if (size >= 2.0) return "large";
                if (size >= 1.2) return "medium";
                if (size <= 0.4) return "tiny";
                return "medium";
            }
            catch { return "medium"; }
        }
        
        private string GetGenderDescription()
        {
            if (pawn?.gender == Gender.Male) return "male";
            if (pawn?.gender == Gender.Female) return "female";
            return "";
        }
    }

    public static class AIGen
    {
        // === Last-generation telemetry (still used by API path) ===
        public static string LastGenerationLog = null;
        public static int GenerationsThisSession = 0;
        private static double totalGenerationSeconds = 0.0;
        public static double AverageGenerationSeconds =>
            GenerationsThisSession == 0 ? 0.0 : totalGenerationSeconds / GenerationsThisSession;
        public static void RecordGenerationSuccess(string pawnLabel, double elapsedSeconds)
        {
            GenerationsThisSession++;
            totalGenerationSeconds += elapsedSeconds;
            LastGenerationLog = string.Format("{0:F1}s for {1}", elapsedSeconds, pawnLabel);
        }

        // Opens the avatar folder in Explorer.
        public static void OpenAvatarFolder()
        {
            try
            {
                string dir = AvatarManager.GetPortraitDir();
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                Messages.Message("Opened avatar folder: " + dir, MessageTypeDefOf.TaskCompletion, historical: false);
            }
            catch (Exception e)
            {
                Messages.Message("Could not open avatar folder: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
            }
        }
    }

    public class AIGenPromptDef : Def
    {
        public string prompt;
        public string overrides = "";
    }

    public class AIAvatarManager : AvatarManager
    {
        public bool useVanillaPortrait = false;
        private Texture2D vanillaPortrait;
        public override Texture2D GetAvatar(bool allowStatic = true)
        {
            if (useVanillaPortrait)
            {
                if (vanillaPortrait == null)
                {
                    RenderTexture active = RenderTexture.active;
                    RenderTexture.active = PortraitsCache.Get(pawn, new Vector2(160, 160), Rot4.South, renderHeadgear: drawHeadgear, renderClothes: drawClothes);
                    float offset = 160 * mod.settings.aiGenVanillaPortraitOffset;
                    vanillaPortrait = new (80, 96);
                    vanillaPortrait.SetPixels(new Color[80*96]);
                    vanillaPortrait.ReadPixels(new Rect(60, offset, 80, 96), 0, 0);
                    vanillaPortrait.Apply();
                    RenderTexture.active = active;
                }
                return vanillaPortrait;
            }
            return base.GetAvatar(allowStatic);
        }
        public override void ClearCachedAvatar()
        {
            if (vanillaPortrait != null)
            {
                UnityEngine.Object.Destroy(vanillaPortrait);
                vanillaPortrait = null;
            }
            base.ClearCachedAvatar();
        }
    }

    public class Prompts_Window : Window
    {
        private AIAvatarManager manager;
        protected string curPrompts;
        protected string curNegative;
        public override Vector2 InitialSize => new Vector2(780f, 520f);
        public Prompts_Window(Pawn pawn, bool drawHeadgear = false, bool drawClothes = true)
        {
            manager = new ();
            manager.SetPawn(pawn);
            manager.SetBGColor(new Color(0,0,0,0));
            manager.drawHeadgear = drawHeadgear;
            manager.drawClothes = drawClothes;
            manager.SetCheckDowned(false);
            curPrompts = manager.pawn.RaceProps.Humanlike ? manager.GetPrompts() : manager.GetCreaturePrompts();
            AvatarSettings s = AvatarManager.mod.settings;
            bool isCreature = !pawn.RaceProps.Humanlike;
            curNegative = isCreature ? BuildNegativePrompt(s, pawn) : AvatarMod.GetFullNegativePrompt(s);
            doCloseX = true;
            draggable = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }
        
        private static string BuildNegativePrompt(AvatarSettings s, Pawn pawn)
        {
            if (!pawn.RaceProps.Humanlike)
            {
                PawnPortraitCategory cat = AvatarManager.ClassifyPawn(pawn);
                switch (cat)
                {
                    case PawnPortraitCategory.Animal:     return s.animalNegativePrompt;
                    case PawnPortraitCategory.Insect:     return s.insectNegativePrompt;
                    case PawnPortraitCategory.Dragon:     return s.dragonNegativePrompt;
                    case PawnPortraitCategory.Aquatic:    return s.aquaticNegativePrompt;
                    case PawnPortraitCategory.Plant:
                    case PawnPortraitCategory.Dryad:      return s.plantNegativePrompt;
                    case PawnPortraitCategory.Mechanoid:  return s.mechNegativePrompt;
                    case PawnPortraitCategory.Undead:
                    case PawnPortraitCategory.Demon:
                    case PawnPortraitCategory.Celestial:
                    case PawnPortraitCategory.Elemental:
                    case PawnPortraitCategory.Construct:
                    case PawnPortraitCategory.Slime:
                    case PawnPortraitCategory.Aberration:
                    case PawnPortraitCategory.Mutant:
                    case PawnPortraitCategory.AnomalyEntity:
                                                          return s.entityNegativePrompt;
                    default:                              return s.otherNegativePrompt;
                }
            }
            return s.apiNegativePrompt;
        }
        public override void DoWindowContents(Rect rect)
        {
            float w = rect.width;
            float margin = 20f;
            float avatarWidth = 80f;
            float leftCol = margin + avatarWidth + 10f;
            float rightColW = 220f;
            float rightColX = w - margin - rightColW;
            float textAreaH = 95f;
            float btnW = 100f;
            float btnH = 32f;
            
            // Row 1: Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(margin, 0f, w - margin * 2, 28f), "AI Portrait Generator");
            Text.Font = GameFont.Small;
            
            // Row 2: Avatar (left) + checkboxes (right)
            GUI.DrawTexture(new Rect(margin, 32f, avatarWidth, 96f), manager.GetAvatar(false));
            
            bool drawHeadgear = manager.drawHeadgear;
            bool drawClothes = manager.drawClothes;
            bool useVanillaPortrait = manager.useVanillaPortrait;
            Widgets.CheckboxLabeled(new Rect(rightColX, 32f, rightColW, 24f), "Use vanilla portrait", ref useVanillaPortrait);
            Widgets.CheckboxLabeled(new Rect(rightColX, 58f, rightColW, 24f), "Draw headgear", ref drawHeadgear, !drawClothes);
            Widgets.CheckboxLabeled(new Rect(rightColX, 84f, rightColW, 24f), "Draw clothes", ref drawClothes);
            manager.useVanillaPortrait = useVanillaPortrait;
            if (drawHeadgear != manager.drawHeadgear) { manager.drawHeadgear = drawHeadgear; curPrompts = manager.pawn.RaceProps.Humanlike ? manager.GetPrompts() : manager.GetCreaturePrompts(); }
            if (drawClothes != manager.drawClothes) { manager.drawClothes = drawClothes; curPrompts = manager.pawn.RaceProps.Humanlike ? manager.GetPrompts() : manager.GetCreaturePrompts(); }
            
            // Row 3: Positive prompt text area
            float posY = 135f;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(margin, posY, w - margin * 2, 16f), "POSITIVE PROMPT (editable):");
            Text.Font = GameFont.Small;
            curPrompts = Widgets.TextArea(new Rect(margin, posY + 16f, w - margin * 2, textAreaH), curPrompts);
            
            // Row 4: Negative prompt text area
            posY = posY + 16f + textAreaH + 10f;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(margin, posY, w - margin * 2, 16f), "NEGATIVE PROMPT (editable):");
            Text.Font = GameFont.Small;
            curNegative = Widgets.TextArea(new Rect(margin, posY + 16f, w - margin * 2, textAreaH), curNegative);
            
            // Row 5: Accept button (right aligned)
            posY = posY + 16f + textAreaH + 10f;
            if (Widgets.ButtonText(new Rect(w - margin - btnW, posY, btnW, btnH), "Generate"))
            {
                DoGenerate();
            }
            
            // Also accept on Enter key
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                DoGenerate();
                Event.current.Use();
            }
        }
        
        private void DoGenerate()
        {
            if (curPrompts.Length == 0)
            {
                Messages.Message("Prompts cannot be empty", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            
            string combinedPrompt = curPrompts;
            if (!string.IsNullOrEmpty(curNegative))
                combinedPrompt += "\n\nAVOID: " + curNegative;
            
            string imagePath = manager.SaveToStaticPortrait();
            string outputPath = imagePath;
            int pawnId = manager.pawn.thingIDNumber;
            string pawnLabel = manager.pawn.LabelShortCap;
            DateTime startedUtc = DateTime.UtcNow;
            bool isCreature = !manager.pawn.RaceProps.Humanlike;

            AvatarMod.MarkPending(pawnId);

            ApiClient.GeneratePortraitAsync(imagePath, combinedPrompt, outputPath, (success, error) =>
            {
                if (success)
                {
                    TextureUtil.RemoveBackground(outputPath);
                    double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                    AvatarMod.MarkAutoGen(pawnId);
                }
                else
                {
                    AvatarMod.RecordFailedAttempt(pawnId);
                    AvatarMod.UnmarkAutoGen(pawnId);
                }
                AvatarMod.UnmarkPending(pawnId);
            }, startedUtc, isCreature: isCreature);

            Messages.Message("AI portrait generation started (API)", MessageTypeDefOf.TaskCompletion, historical: false);
            Find.WindowStack.TryRemove(this);
        }
        public override void PostClose()
        {
            manager.ClearCachedAvatar();
        }
    }
}
