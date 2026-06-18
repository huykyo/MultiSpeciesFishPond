using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.GameData.FishPonds;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Extensions;
using StardewValley.Internal;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using HarmonyLib;

namespace MultiSpeciesFishPond
{
    public class ModEntry : Mod
    {
        private Dictionary<string, PondData> pondDataDict = new Dictionary<string, PondData>();
        public static ModEntry Instance;
        public static bool IsPondQueryMenuDrawing = false;
        public static int FishCountCallCount = 0;
        public static StardewValley.Object OriginalFishItem = null;

        // Optimized name cache and item cache
        public static Dictionary<string, string> NameCache = new Dictionary<string, string>();
        public static Dictionary<string, Item> FishItemCache = new Dictionary<string, Item>();

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            Monitor.Log("Multi Species Fish Pond loaded!", LogLevel.Info);

            var harmony = new Harmony(ModManifest.UniqueID);
            harmony.PatchAll();



            Helper.ConsoleCommands.Add("list_ponds", "Lists all fish ponds and their coordinates", (cmd, args) =>
            {
                if (Game1.currentLocation == null)
                {
                    Monitor.Log("Please load a save game first.", LogLevel.Error);
                    return;
                }
                Monitor.Log("Scanning for fish ponds...", LogLevel.Info);
                int count = 0;
                Utility.ForEachLocation(location =>
                {
                    foreach (var building in location.buildings)
                    {
                        if (building is FishPond pond)
                        {
                            count++;
                            string key = GetPondKey(pond);
                            string fishList = "";
                            PondData data = GetOrCreatePondData(pond);
                            foreach (var entry in data.FishCounts)
                            {
                                fishList += $"{entry.Key}:{entry.Value} ";
                            }
                            Monitor.Log($"Pond #{count} at {location.Name} ({pond.tileX.Value}, {pond.tileY.Value}) - Native: {pond.fishType.Value} - Mod Data: {fishList}", LogLevel.Info);
                        }
                    }
                    return true;
                });
                Monitor.Log($"Found {count} fish ponds total.", LogLevel.Info);
            });
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            pondDataDict = Helper.Data.ReadSaveData<Dictionary<string, PondData>>("PondData")
                           ?? new Dictionary<string, PondData>();

            // Đồng bộ cá gốc trong ao vào dữ liệu mod khi tải game
            Utility.ForEachLocation(location =>
            {
                foreach (var building in location.buildings)
                {
                    if (!(building is FishPond pond)) continue;

                    // Log coordinates of all found ponds during save load
                    Monitor.Log($"[MultiSpeciesFishPond] Found pond at ({pond.tileX.Value}, {pond.tileY.Value}) - native fish: {pond.fishType.Value}, occupants: {pond.currentOccupants.Value}", LogLevel.Info);

                    if (pond.fishType.Value == null || pond.fishType.Value == "") continue;

                    GetOrCreatePondData(pond);
                }
                return true;
            });

            Monitor.Log($"Loaded pond data: {pondDataDict.Count} ponds", LogLevel.Info);
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            Helper.Data.WriteSaveData("PondData", pondDataDict);
        }

        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (!(Game1.activeClickableMenu is PondQueryMenu pondMenu)) return;

            FishPond pond = Helper.Reflection
                .GetField<FishPond>(pondMenu, "_pond")
                .GetValue();

            if (pond == null) return;

            PondData data = GetOrCreatePondData(pond);

            // Vẽ danh sách loài cá lên góc menu
            int x = pondMenu.xPositionOnScreen + 640 + 20;
            int y = pondMenu.yPositionOnScreen;

            IClickableMenu.drawTextureBox(
                e.SpriteBatch,
                x, y, 400, 40 + data.FishCounts.Count * 30 + 40,
                Color.White
            );

            e.SpriteBatch.DrawString(
                Game1.smallFont,
                ModEntry.Instance.Helper.Translation.Get("species-count", new { count = data.FishCounts.Count, max = PondData.MaxSpecies }).ToString(),
                new Vector2(x + 16, y + 16),
                Color.Black
            );

            int lineY = y + 46;
            foreach (var entry in data.FishCounts)
            {
                string fishId = entry.Key;
                string fishName = GetFishName(fishId);
                string suffix = "";

                if (data.NeededItems.TryGetValue(fishId, out string neededItemQid) && neededItemQid != null)
                {
                    string itemName = GetFishName(neededItemQid);
                    int count = data.NeededItemCounts.ContainsKey(fishId) ? data.NeededItemCounts[fishId] : 1;
                    suffix = ModEntry.Instance.Helper.Translation.Get("needs-item", new { count = count, itemName = itemName }).ToString();
                }

                e.SpriteBatch.DrawString(
                    Game1.smallFont,
                    $"- {fishName}: {entry.Value}{suffix}",
                    new Vector2(x + 16, lineY),
                    Color.DarkSlateGray
                );
                lineY += 30;
            }
        }

        public static string GetFishName(string itemId)
        {
            string langCode = LocalizedContentManager.CurrentLanguageCode.ToString();
            string cacheKey = $"{itemId}_{langCode}";
            if (NameCache.TryGetValue(cacheKey, out string cachedName))
            {
                return cachedName;
            }
            try
            {
                var data = ItemRegistry.GetData(itemId);
                string name = data?.DisplayName ?? itemId;
                NameCache[cacheKey] = name;
                return name;
            }
            catch
            {
                return itemId;
            }
        }

        public static Item GetCachedFishItem(string fishId)
        {
            if (FishItemCache.TryGetValue(fishId, out Item item))
            {
                return item;
            }
            try
            {
                item = new StardewValley.Object(fishId, 1);
                FishItemCache[fishId] = item;
                return item;
            }
            catch
            {
                return null;
            }
        }

        public static string GetPondKey(FishPond pond)
        {
            if (pond.modData == null)
            {
                return $"{pond.tileX.Value}_{pond.tileY.Value}";
            }

            if (pond.modData.TryGetValue("MultiSpeciesFishPond.id", out string id))
            {
                return id;
            }

            string legacyKey = $"{pond.tileX.Value}_{pond.tileY.Value}";
            string newId = Guid.NewGuid().ToString();

            if (Instance != null && Instance.pondDataDict != null && Instance.pondDataDict.TryGetValue(legacyKey, out var legacyData))
            {
                Instance.pondDataDict[newId] = legacyData;
                Instance.pondDataDict.Remove(legacyKey);
                Instance.Monitor.Log($"[MultiSpeciesFishPond] Migrated legacy pond data at {legacyKey} to new ID {newId}", LogLevel.Info);
            }
            else
            {
                Instance?.Monitor.Log($"[MultiSpeciesFishPond] Initialized new pond ID {newId} at ({pond.tileX.Value}, {pond.tileY.Value})", LogLevel.Info);
            }

            pond.modData["MultiSpeciesFishPond.id"] = newId;
            return newId;
        }

        public PondData GetOrCreatePondData(FishPond pond)
        {
            string key = GetPondKey(pond);
            if (!pondDataDict.ContainsKey(key))
                pondDataDict[key] = new PondData();

            var data = pondDataDict[key];
            data.InitializeDefaults();

            // Đồng bộ cá gốc ngay lập tức vào dữ liệu mod nếu chưa có
            if (pond.fishType.Value != null && pond.fishType.Value != "")
            {
                string nativeFishId = pond.fishType.Value;
                if (!data.FishCounts.ContainsKey(nativeFishId))
                {
                    int otherFishCount = 0;
                    foreach (var entry in data.FishCounts)
                    {
                        if (entry.Key != nativeFishId)
                        {
                            otherFishCount += entry.Value;
                        }
                    }
                    int nativeFishCount = Math.Max(0, pond.FishCount - otherFishCount);
                    if (nativeFishCount == 0 && otherFishCount == 0)
                    {
                        nativeFishCount = pond.FishCount;
                    }
                    data.FishCounts[nativeFishId] = nativeFishCount;

                    if (!data.LastUnlockedGates.ContainsKey(nativeFishId))
                    {
                        data.LastUnlockedGates[nativeFishId] = pond.lastUnlockedPopulationGate.Value;
                    }
                    if (!data.DaysSinceSpawn.ContainsKey(nativeFishId))
                    {
                        data.DaysSinceSpawn[nativeFishId] = pond.daysSinceSpawn.Value;
                    }
                    if (pond.neededItem.Value != null && !data.NeededItems.ContainsKey(nativeFishId))
                    {
                        data.NeededItems[nativeFishId] = pond.neededItem.Value.QualifiedItemId;
                        data.NeededItemCounts[nativeFishId] = pond.neededItemCount.Value;
                    }
                }
            }

            // Đồng bộ số lượng cá trong ao về tổng số cá của các loài có trong ao
            int totalFish = data.TotalFish();
            if (pond.currentOccupants.Value != totalFish)
            {
                pond.currentOccupants.Value = totalFish;
            }

            return data;
        }

        public static int GetMaxOccupantsForSpecies(string fishId, int lastUnlockedGate)
        {
            FishPondData data = FishPond.GetRawData(fishId);
            if (data == null)
            {
                return 10;
            }
            if (data.MaxPopulation > 0)
            {
                return data.MaxPopulation;
            }
            for (int i = 1; i <= 10; i++)
            {
                if (i <= lastUnlockedGate)
                {
                    continue;
                }
                if (!(data.PopulationGates?.ContainsKey(i) ?? false))
                {
                    continue;
                }
                return i - 1;
            }
            return 10;
        }

        public static bool TryGetNeededItemDataForSpecies(FishPond pond, string fishId, int currentCount, int maxOccupantsForSpecies, int lastUnlockedGate, out string itemId, out int count)
        {
            itemId = null;
            count = 1;
            if (currentCount < maxOccupantsForSpecies)
            {
                return false;
            }
            FishPondData data = FishPond.GetRawData(fishId);
            if (data?.PopulationGates != null)
            {
                if (maxOccupantsForSpecies + 1 <= lastUnlockedGate)
                {
                    return false;
                }
                if (data.PopulationGates.TryGetValue(maxOccupantsForSpecies + 1, out var gate))
                {
                    Random r = Utility.CreateDaySaveRandom(Utility.CreateRandomSeed(pond.tileX.Value * 1000, pond.tileY.Value * 2000));
                    string[] split_data = ArgUtility.SplitBySpace(r.ChooseFrom(gate));
                    if (split_data.Length >= 1)
                    {
                        itemId = split_data[0];
                    }
                    if (split_data.Length >= 3)
                    {
                        count = r.Next(Convert.ToInt32(split_data[1]), Convert.ToInt32(split_data[2]) + 1);
                    }
                    else if (split_data.Length >= 2)
                    {
                        count = Convert.ToInt32(split_data[1]);
                    }
                    return true;
                }
            }
            return false;
        }

        public static Item GetFishProduceForSpecies(FishPond pond, string fishId, Random random = null)
        {
            if (random == null)
            {
                random = Game1.random;
            }
            FishPondData data = FishPond.GetRawData(fishId);
            if (data == null)
            {
                return null;
            }
            GameLocation location = pond.GetParentLocation() ?? Game1.getFarm();
            Item cachedFish = GetCachedFishItem(fishId);
            StardewValley.Object fish = cachedFish as StardewValley.Object;
            if (fish == null)
            {
                fish = new StardewValley.Object(fishId, 1);
            }
            FishPondReward selectedOutput = null;

            PondData pondData = Instance.GetOrCreatePondData(pond);
            int speciesCount = pondData.FishCounts.ContainsKey(fishId) ? pondData.FishCounts[fishId] : 0;

            foreach (FishPondReward itemData in data.ProducedItems)
            {
                if (!(selectedOutput?.Precedence <= itemData.Precedence) && 
                    speciesCount >= itemData.RequiredPopulation && 
                    random.NextBool(itemData.Chance) && 
                    GameStateQuery.CheckConditions(itemData.Condition, location, null, null, fish))
                {
                    selectedOutput = itemData;
                }
            }
            Item item = null;
            if (selectedOutput != null)
            {
                item = ItemQueryResolver.TryResolveRandomItem(selectedOutput, new ItemQueryContext(location, null, null, $"fish pond data '{fishId}' > reward '{selectedOutput.Id}'"), avoidRepeat: false, null, (string id) => (!(ItemRegistry.QualifyItemId(selectedOutput.ItemId) == "(O)812")) ? id : ("FLAVORED_ITEM Roe " + fish.QualifiedItemId), fish);
            }
            if (item != null)
            {
                if (item.Name.Contains("Roe"))
                {
                    while (random.NextDouble() < 0.2)
                    {
                        item.Stack++;
                    }
                }
                if (pond.goldenAnimalCracker.Value)
                {
                    item.Stack *= 2;
                }
            }
            return item;
        }

        public static int GetTotalCapacity(FishPond pond, PondData data)
        {
            if (data.FishCounts.Count == 0)
            {
                return 10;
            }
            int total = 0;
            foreach (var entry in data.FishCounts)
            {
                string fishId = entry.Key;
                int lastUnlockedGate = data.LastUnlockedGates.ContainsKey(fishId) ? data.LastUnlockedGates[fishId] : 0;
                total += GetMaxOccupantsForSpecies(fishId, lastUnlockedGate);
            }
            return total;
        }
    }

    public class PendingItem
    {
        public string QualifiedItemId { get; set; }
        public int Stack { get; set; }
        public string PreserveType { get; set; }
        public string PreservedParentSheetIndex { get; set; }

        public PendingItem() { }

        public PendingItem(Item item)
        {
            QualifiedItemId = item.QualifiedItemId;
            Stack = item.Stack;
            if (item is StardewValley.Object obj)
            {
                if (obj.preserve.Value != null)
                {
                    PreserveType = obj.preserve.Value.ToString();
                }
                PreservedParentSheetIndex = obj.preservedParentSheetIndex.Value;
            }
        }

        public Item CreateItem()
        {
            if (!string.IsNullOrEmpty(PreserveType) && !string.IsNullOrEmpty(PreservedParentSheetIndex))
            {
                string parentId = PreservedParentSheetIndex;
                if (!parentId.StartsWith("("))
                {
                    parentId = "(O)" + parentId;
                }
                var context = new ItemQueryContext(Game1.currentLocation ?? Game1.getFarm(), null, null, "multi_species_fish_pond");
                Item item = ItemQueryResolver.TryResolveRandomItem($"FLAVORED_ITEM {PreserveType} {parentId}", context);
                if (item != null)
                {
                    item.Stack = Stack;
                    return item;
                }
            }
            return ItemRegistry.Create(QualifiedItemId, Stack);
        }
    }

    public class PondData
    {
        public const int MaxSpecies = 10;

        public Dictionary<string, int> FishCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> LastUnlockedGates { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> DaysSinceSpawn { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> NeededItems { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> NeededItemCounts { get; set; } = new Dictionary<string, int>();
        public List<PendingItem> PendingOutputs { get; set; } = new List<PendingItem>();

        public bool CanAddSpecies => FishCounts.Count < MaxSpecies;

        public void InitializeDefaults()
        {
            if (FishCounts == null) FishCounts = new Dictionary<string, int>();
            if (LastUnlockedGates == null) LastUnlockedGates = new Dictionary<string, int>();
            if (DaysSinceSpawn == null) DaysSinceSpawn = new Dictionary<string, int>();
            if (NeededItems == null) NeededItems = new Dictionary<string, string>();
            if (NeededItemCounts == null) NeededItemCounts = new Dictionary<string, int>();
            if (PendingOutputs == null) PendingOutputs = new List<PendingItem>();
        }

        public bool AddFish(string fishId, int count = 1)
        {
            InitializeDefaults();
            if (!FishCounts.ContainsKey(fishId))
            {
                if (!CanAddSpecies) return false;
                FishCounts[fishId] = 0;
            }
            FishCounts[fishId] += count;

            if (!LastUnlockedGates.ContainsKey(fishId)) LastUnlockedGates[fishId] = 0;
            if (!DaysSinceSpawn.ContainsKey(fishId)) DaysSinceSpawn[fishId] = 0;

            return true;
        }

        public int TotalFish()
        {
            InitializeDefaults();
            int total = 0;
            foreach (var count in FishCounts.Values)
                total += count;
            return total;
        }
    }

    [HarmonyPatch(typeof(FishPond), "doAction")]
    public class FishPond_doAction_Patch
    {
        public static bool Prefix(FishPond __instance, Vector2 tileLocation, Farmer who, ref bool __result)
        {
            // Ngăn chặn ao cá chặn click chuột khi người chơi muốn tương tác với hòm đồ hoặc máy móc khác đặt trên cùng ô tile.
            if (who.currentLocation != null && who.currentLocation.Objects.ContainsKey(tileLocation))
            {
                __result = false;
                return false;
            }

            if (!__instance.occupiesTile(tileLocation)) return true;
            if (who.ActiveObject == null) return true;

            // If the player is holding the item needed for the active quest, let vanilla handle it!
            if (__instance.HasUnresolvedNeeds() && __instance.neededItem.Value != null && who.ActiveObject.QualifiedItemId == __instance.neededItem.Value.QualifiedItemId)
            {
                return true;
            }

            // Check if active item is a fish or legal input
            bool isFishInput = who.ActiveObject.Category == -4 || who.ActiveObject.QualifiedItemId == "(O)393" || who.ActiveObject.QualifiedItemId == "(O)397";
            if (!isFishInput) return true;

            string fishId = who.ActiveObject.ItemId;
            StardewValley.Object activeFish = who.ActiveObject;

            // Log details
            float distance = Vector2.Distance(who.Position, __instance.GetCenterTile() * 64f);
            ModEntry.Instance.Monitor.Log($"[MultiSpeciesFishPond] Interaction event triggered: Player={who.Name}, PlayerPos={who.Position}, ClickedTile={tileLocation}, TargetPond=({__instance.tileX.Value}, {__instance.tileY.Value}), DistanceToPondCenter={distance}px, Fish={activeFish.DisplayName}", LogLevel.Info);

            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);

            // If the species is already in the pond
            if (data.FishCounts.ContainsKey(fishId))
            {
                int count = data.FishCounts[fishId];
                int lastUnlockedGate = data.LastUnlockedGates.ContainsKey(fishId) ? data.LastUnlockedGates[fishId] : 0;
                int maxOccupantsForSpecies = ModEntry.GetMaxOccupantsForSpecies(fishId, lastUnlockedGate);

                if (count >= maxOccupantsForSpecies)
                {
                    if (maxOccupantsForSpecies >= 10)
                    {
                        Game1.drawObjectDialogue(ModEntry.Instance.Helper.Translation.Get("species-limit-reached", new { fishName = activeFish.DisplayName }).ToString());
                    }
                    else
                    {
                        Game1.drawObjectDialogue(ModEntry.Instance.Helper.Translation.Get("needs-items-to-grow", new { fishName = activeFish.DisplayName }).ToString());
                    }
                    __result = true;
                    return false;
                }

                data.AddFish(fishId, 1);
                var method = typeof(FishPond).GetMethod("showObjectThrownIntoPondAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                method?.Invoke(__instance, new object[] { who, activeFish, null });

                who.reduceActiveItemByOne();
                __instance.currentOccupants.Value = data.TotalFish();
                __result = true;
                return false;
            }

            // If it's a new species, check if we've reached the species limit (10)
            if (!data.CanAddSpecies)
            {
                Game1.addHUDMessage(new HUDMessage(ModEntry.Instance.Helper.Translation.Get("pond-full-species").ToString(), HUDMessage.error_type));
                __result = true;
                return false;
            }

            string fishName = activeFish.DisplayName;
            string newFishId = fishId;
            FishPond pond = __instance;

            ModEntry.Instance.Monitor.Log($"[MultiSpeciesFishPond] Showing confirmation dialog for pond at ({pond.tileX.Value}, {pond.tileY.Value}) to add fish '{fishName}'", LogLevel.Info);

            Game1.activeClickableMenu = new ConfirmationDialog(
                ModEntry.Instance.Helper.Translation.Get("add-fish-confirm", new { fishName = fishName }).ToString(),
                (farmer) =>
                {
                    ModEntry.Instance.Monitor.Log($"[MultiSpeciesFishPond] Confirmation YES clicked. Adding fish '{fishName}' to pond at ({pond.tileX.Value}, {pond.tileY.Value})", LogLevel.Info);
                    data.AddFish(newFishId, 1);
                    
                    var method = typeof(FishPond).GetMethod("showObjectThrownIntoPondAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                    method?.Invoke(pond, new object[] { who, activeFish, null });

                    who.reduceActiveItemByOne();
                    
                    if (pond.fishType.Value == "" || pond.fishType.Value == null)
                    {
                        pond.fishType.Value = newFishId;
                        pond.UpdateMaximumOccupancy();
                    }

                    pond.currentOccupants.Value = data.TotalFish();
                    Game1.activeClickableMenu = null;
                },
                (farmer) =>
                {
                    ModEntry.Instance.Monitor.Log($"[MultiSpeciesFishPond] Confirmation NO clicked for pond at ({pond.tileX.Value}, {pond.tileY.Value})", LogLevel.Info);
                    Game1.activeClickableMenu = null;
                }
            );

            __result = true;
            return false;
        }

        public static void Postfix(FishPond __instance, Vector2 tileLocation, Farmer who, bool __result)
        {
            if (__instance.output.Value == null)
            {
                PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);
                if (data.PendingOutputs.Count > 0)
                {
                    while (data.PendingOutputs.Count > 0)
                    {
                        PendingItem pending = data.PendingOutputs[0];
                        Item item = pending.CreateItem();

                        if (who.addItemToInventoryBool(item))
                        {
                            Game1.playSound("coin");
                            int bonusExperience = 0;
                            if (item is StardewValley.Object obj)
                            {
                                bonusExperience = (int)((float)obj.sellToStorePrice(-1L) * FishPond.HARVEST_OUTPUT_EXP_MULTIPLIER);
                            }
                            who.gainExperience(1, bonusExperience + FishPond.HARVEST_BASE_EXP);
                            data.PendingOutputs.RemoveAt(0);
                        }
                        else
                        {
                            __instance.output.Value = item;
                            break;
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(FishPond), nameof(FishPond.dayUpdate))]
    public class FishPond_dayUpdate_Patch
    {
        private static int originalOccupantsValue = 0;

        public static bool Prefix(FishPond __instance, int dayOfMonth)
        {
            // Tạm thời đặt currentOccupants về 0 để bỏ qua logic cập nhật ngày của vanilla FishPond.
            // Bằng cách trả về true, game sẽ gọi base.dayUpdate(dayOfMonth) của Building một cách an toàn.
            originalOccupantsValue = __instance.currentOccupants.Value;
            __instance.currentOccupants.Value = 0;
            return true;
        }

        public static void Postfix(FishPond __instance, int dayOfMonth)
        {
            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);
            // Khôi phục lại số lượng cá gốc từ dữ liệu mod
            __instance.currentOccupants.Value = data.TotalFish();

            __instance.hasSpawnedFish.Value = false;
            __instance.GetType().GetField("_hasAnimatedSpawnedFish", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(__instance, false);

            if (__instance.hasCompletedRequest.Value)
            {
                __instance.neededItem.Value = null;
                __instance.neededItemCount.Set(-1);
                __instance.hasCompletedRequest.Value = false;
            }

            if (__instance.currentOccupants.Value > 0 && data.FishCounts.Count > 0)
            {
                foreach (var entry in data.FishCounts)
                {
                    string fishId = entry.Key;
                    int count = entry.Value;
                    if (count <= 0) continue;

                    FishPondData fishData = FishPond.GetRawData(fishId);
                    if (fishData == null) continue;

                    int seed = __instance.tileX.Value * 1000 + __instance.tileY.Value * 2000 + fishId.GetHashCode();
                    Random r = Utility.CreateDaySaveRandom(seed);

                    double chance = (fishData.BaseMinProduceChance >= fishData.BaseMaxProduceChance)
                        ? fishData.BaseMinProduceChance
                        : Utility.Lerp(fishData.BaseMinProduceChance, fishData.BaseMaxProduceChance, (float)count / 10f);

                    if (r.NextDouble() < chance)
                    {
                        Item producedItem = ModEntry.GetFishProduceForSpecies(__instance, fishId, r);
                        if (producedItem != null)
                        {
                            data.PendingOutputs.Add(new PendingItem(producedItem));
                        }
                    }
                }

                if (__instance.output.Value == null && data.PendingOutputs.Count > 0)
                {
                    __instance.output.Value = data.PendingOutputs[0].CreateItem();
                    data.PendingOutputs.RemoveAt(0);
                }

                List<string> speciesKeys = new List<string>(data.FishCounts.Keys);
                foreach (string fishId in speciesKeys)
                {
                    FishPondData fishData = FishPond.GetRawData(fishId);
                    if (fishData == null) continue;

                    if (!data.DaysSinceSpawn.ContainsKey(fishId)) data.DaysSinceSpawn[fishId] = 0;
                    if (!data.LastUnlockedGates.ContainsKey(fishId)) data.LastUnlockedGates[fishId] = 0;

                    data.DaysSinceSpawn[fishId]++;
                    if (data.DaysSinceSpawn[fishId] > fishData.SpawnTime)
                    {
                        data.DaysSinceSpawn[fishId] = fishData.SpawnTime;
                    }

                    if (data.DaysSinceSpawn[fishId] >= fishData.SpawnTime)
                    {
                        int currentCount = data.FishCounts[fishId];
                        int lastUnlockedGate = data.LastUnlockedGates[fishId];
                        int maxOccupantsForSpecies = ModEntry.GetMaxOccupantsForSpecies(fishId, lastUnlockedGate);

                        if (currentCount >= maxOccupantsForSpecies)
                        {
                            if (!data.NeededItems.ContainsKey(fishId) || data.NeededItems[fishId] == null)
                            {
                                if (ModEntry.TryGetNeededItemDataForSpecies(__instance, fishId, currentCount, maxOccupantsForSpecies, lastUnlockedGate, out string itemId, out int neededCount))
                                {
                                    data.NeededItems[fishId] = itemId;
                                    data.NeededItemCounts[fishId] = neededCount;
                                    data.DaysSinceSpawn[fishId] = 0;
                                }
                            }
                        }
                        else
                        {
                            data.FishCounts[fishId]++;
                            __instance.currentOccupants.Value++;
                            __instance.hasSpawnedFish.Value = true;
                            data.DaysSinceSpawn[fishId] = 0;
                        }
                    }
                }

                FishPond_ResolveNeeds_Patch.SyncActiveQuestToVanilla(__instance, data);

                int crabCount = data.FishCounts.ContainsKey("717") ? data.FishCounts["717"] : 0;
                if (crabCount == 10)
                {
                    foreach (Farmer f in Game1.getAllFarmers())
                    {
                        if (f.mailReceived.Add("FullCrabPond"))
                        {
                            f.activeDialogueEvents["FullCrabPond"] = 14;
                        }
                    }
                }

                DoFishSpecificWaterColoringForPond(__instance, data);
            }
        }

        private static void DoFishSpecificWaterColoringForPond(FishPond pond, PondData data)
        {
            if (data.FishCounts.Count == 0)
            {
                pond.overrideWaterColor.Value = Color.White;
                return;
            }

            List<Color> possibleColors = new List<Color>();

            foreach (var fishId in data.FishCounts.Keys)
            {
                FishPondData fishData = FishPond.GetRawData(fishId);
                if (fishData != null && fishData.WaterColor?.Count > 0)
                {
                    int speciesOccupants = data.FishCounts.ContainsKey(fishId) ? data.FishCounts[fishId] : 0;
                    if (speciesOccupants <= 0) continue;

                    int lastUnlockedGate = data.LastUnlockedGates.ContainsKey(fishId) ? data.LastUnlockedGates[fishId] : 0;
                    Item cachedFish = ModEntry.GetCachedFishItem(fishId);
                    StardewValley.Object fishObject = cachedFish as StardewValley.Object;

                    if (fishObject != null)
                    {
                        GameLocation location = pond.GetParentLocation() ?? Game1.getFarm();
                        foreach (FishPondWaterColor entry in fishData.WaterColor)
                        {
                            if (speciesOccupants >= entry.MinPopulation && lastUnlockedGate >= entry.MinUnlockedPopulationGate && (entry.Condition == null || GameStateQuery.CheckConditions(entry.Condition, location, null, null, fishObject)))
                            {
                                Color? color = null;
                                if (string.Equals(entry.Color, "CopyFromInput", StringComparison.OrdinalIgnoreCase))
                                {
                                    color = ItemContextTagManager.GetColorFromTags(fishObject);
                                }
                                else
                                {
                                    color = Utility.StringToColor(entry.Color);
                                }
                                if (color.HasValue && color.Value != Color.White)
                                {
                                    possibleColors.Add(color.Value);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (possibleColors.Count > 0)
            {
                int day = Game1.dayOfMonth;
                int seed = day + pond.tileX.Value + pond.tileY.Value;
                Color chosenColor = possibleColors[seed % possibleColors.Count];
                pond.overrideWaterColor.Value = chosenColor;
            }
            else
            {
                pond.overrideWaterColor.Value = Color.White;
            }
        }
    }

    [HarmonyPatch(typeof(FishPond), nameof(FishPond.ResolveNeeds))]
    public class FishPond_ResolveNeeds_Patch
    {
        public static void Postfix(FishPond __instance, Farmer who)
        {
            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);
            string activeFishId = __instance.fishType.Value;

            if (activeFishId != null && data.NeededItems.ContainsKey(activeFishId))
            {
                data.LastUnlockedGates[activeFishId] = __instance.lastUnlockedPopulationGate.Value;

                data.NeededItems[activeFishId] = null;
                data.NeededItemCounts[activeFishId] = -1;

                ModEntry.Instance.Monitor.Log($"Unlocked gate for species {activeFishId} (level {data.LastUnlockedGates[activeFishId]})", LogLevel.Info);

                SyncActiveQuestToVanilla(__instance, data);
            }
        }

        public static void SyncActiveQuestToVanilla(FishPond pond, PondData data)
        {
            string activeQuestFishId = null;

            foreach (var entry in data.NeededItems)
            {
                if (entry.Value != null)
                {
                    activeQuestFishId = entry.Key;
                    break;
                }
            }

            if (activeQuestFishId != null)
            {
                pond.neededItem.Value = ItemRegistry.Create(data.NeededItems[activeQuestFishId]);
                pond.neededItemCount.Value = data.NeededItemCounts[activeQuestFishId];
                pond.fishType.Value = activeQuestFishId;
                pond.lastUnlockedPopulationGate.Value = data.LastUnlockedGates[activeQuestFishId];
                pond.maxOccupants.Value = ModEntry.GetMaxOccupantsForSpecies(activeQuestFishId, data.LastUnlockedGates[activeQuestFishId]);
            }
            else
            {
                pond.neededItem.Value = null;
                pond.neededItemCount.Value = -1;

                if (data.FishCounts.Count > 0)
                {
                    foreach (var firstKey in data.FishCounts.Keys)
                    {
                        pond.fishType.Value = firstKey;
                        break;
                    }
                }
                else
                {
                    pond.fishType.Value = null;
                }
                pond.UpdateMaximumOccupancy();
            }
        }
    }

    [HarmonyPatch(typeof(FishPond), nameof(FishPond.CatchFish))]
    public class FishPond_CatchFish_Patch
    {
        public static bool Prefix(FishPond __instance, ref StardewValley.Object __result)
        {
            if (__instance.currentOccupants.Value == 0)
            {
                __result = null;
                return false;
            }

            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);
            if (data.FishCounts.Count == 0)
            {
                __result = null;
                return false;
            }

            List<string> speciesList = new List<string>();
            foreach (var entry in data.FishCounts)
            {
                for (int i = 0; i < entry.Value; i++)
                {
                    speciesList.Add(entry.Key);
                }
            }

            if (speciesList.Count == 0)
            {
                __result = null;
                return false;
            }

            string chosenFishId = Game1.random.ChooseFrom(speciesList);

            data.FishCounts[chosenFishId]--;
            if (data.FishCounts[chosenFishId] <= 0)
            {
                data.FishCounts.Remove(chosenFishId);
                data.NeededItems.Remove(chosenFishId);
                data.NeededItemCounts.Remove(chosenFishId);
                data.DaysSinceSpawn.Remove(chosenFishId);
                data.LastUnlockedGates.Remove(chosenFishId);
            }

            __instance.currentOccupants.Value = data.TotalFish();
            if (data.FishCounts.Count > 0)
            {
                if (__instance.fishType.Value == chosenFishId && !data.FishCounts.ContainsKey(chosenFishId))
                {
                    foreach (var firstKey in data.FishCounts.Keys)
                    {
                        __instance.fishType.Value = firstKey;
                        break;
                    }
                }
            }
            else
            {
                __instance.fishType.Value = null;
            }

            // Sync active quest and capacity limits to vanilla immediately
            FishPond_ResolveNeeds_Patch.SyncActiveQuestToVanilla(__instance, data);

            __instance.UpdateMaximumOccupancy();

            __result = new StardewValley.Object(chosenFishId, 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(PondQueryMenu), nameof(PondQueryMenu.draw))]
    public class PondQueryMenu_draw_Patch
    {
        private static int originalMaxOccupants = -1;

        public static void Prefix(PondQueryMenu __instance)
        {
            FishPond pond = ModEntry.Instance.Helper.Reflection
                .GetField<FishPond>(__instance, "_pond")
                .GetValue();

            var field = typeof(PondQueryMenu).GetField("_fishItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && pond != null)
            {
                var original = field.GetValue(__instance) as StardewValley.Object;
                if (original != null && !(original is InvisibleFishObject))
                {
                    ModEntry.OriginalFishItem = original;
                    PondData data = ModEntry.Instance.GetOrCreatePondData(pond);
                    var invisible = new InvisibleFishObject(original.ItemId, original.DisplayName, __instance, data);
                    field.SetValue(__instance, invisible);
                }
            }

            if (pond != null)
            {
                originalMaxOccupants = pond.maxOccupants.Value;
                PondData data = ModEntry.Instance.GetOrCreatePondData(pond);
                pond.currentOccupants.Value = data.TotalFish();
                pond.maxOccupants.Value = ModEntry.GetTotalCapacity(pond, data);
            }
        }

        public static void Postfix(PondQueryMenu __instance, SpriteBatch b)
        {
            FishPond pond = ModEntry.Instance.Helper.Reflection
                .GetField<FishPond>(__instance, "_pond")
                .GetValue();
            if (pond != null && originalMaxOccupants != -1)
            {
                pond.maxOccupants.Value = originalMaxOccupants;
                originalMaxOccupants = -1;
            }

            // Khôi phục lại fishItem gốc
            var field = typeof(PondQueryMenu).GetField("_fishItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && ModEntry.OriginalFishItem != null)
            {
                field.SetValue(__instance, ModEntry.OriginalFishItem);
                ModEntry.OriginalFishItem = null;
            }
        }
    }

    [HarmonyPatch(typeof(PondQueryMenu), nameof(PondQueryMenu.performHoverAction))]
    public class PondQueryMenu_performHoverAction_Patch
    {
        public static void Postfix(PondQueryMenu __instance, int x, int y)
        {
            FishPond pond = ModEntry.Instance.Helper.Reflection
                .GetField<FishPond>(__instance, "_pond")
                .GetValue();
            if (pond == null) return;

            PondData data = ModEntry.Instance.GetOrCreatePondData(pond);
            if (data.FishCounts.Count == 0) return;

            int maxIconsPerRow = 5;
            int spacing = 56;
            int iconSize = 48;
            int rowHeight = 54;
            int startY = __instance.yPositionOnScreen + 275;

            // Phân bổ danh sách cá thành các hàng tối đa 5 icon
            List<List<KeyValuePair<string, int>>> rows = new List<List<KeyValuePair<string, int>>>();
            List<KeyValuePair<string, int>> currentRow = new List<KeyValuePair<string, int>>();
            foreach (var entry in data.FishCounts)
            {
                currentRow.Add(entry);
                if (currentRow.Count == maxIconsPerRow)
                {
                    rows.Add(currentRow);
                    currentRow = new List<KeyValuePair<string, int>>();
                }
            }
            if (currentRow.Count > 0)
            {
                rows.Add(currentRow);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int nRow = row.Count;
                int rowWidth = (nRow - 1) * spacing + iconSize;
                int startX = __instance.xPositionOnScreen + ((IClickableMenu)__instance).width / 2 - rowWidth / 2;

                for (int i = 0; i < nRow; i++)
                {
                    var entry = row[i];
                    string fishId = entry.Key;
                    int iconX = startX + i * spacing;
                    int iconY = startY + r * rowHeight;

                    if (x >= iconX && x <= iconX + iconSize && y >= iconY && y <= iconY + iconSize)
                    {
                        string displayName = ModEntry.GetFishName(fishId);
                        
                        // Chi tiết yêu cầu vật phẩm nếu có
                        string extraInfo = "";
                        if (data.NeededItems.TryGetValue(fishId, out string neededItemQid) && neededItemQid != null)
                        {
                            string itemName = ModEntry.GetFishName(neededItemQid);
                            int count = data.NeededItemCounts.ContainsKey(fishId) ? data.NeededItemCounts[fishId] : 1;
                            extraInfo = "\n" + ModEntry.Instance.Helper.Translation.Get("needs-item-grow", new { count = count, itemName = itemName }).ToString();
                        }
                        else
                        {
                            int currentCount = entry.Value;
                            int lastUnlockedGate = data.LastUnlockedGates.ContainsKey(fishId) ? data.LastUnlockedGates[fishId] : 0;
                            int maxOccupants = ModEntry.GetMaxOccupantsForSpecies(fishId, lastUnlockedGate);
                            extraInfo = "\n" + ModEntry.Instance.Helper.Translation.Get("capacity-info", new { count = currentCount, max = maxOccupants }).ToString();
                        }

                        var hoverField = typeof(PondQueryMenu).GetField("hoverText", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (hoverField != null)
                        {
                            hoverField.SetValue(__instance, displayName + extraInfo);
                        }
                        return;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(FishPond), nameof(FishPond.Update))]
    public class FishPond_Update_Patch
    {
        private static System.Runtime.CompilerServices.ConditionalWeakTable<JumpingFish, StardewValley.Object> jumpingFishMap = 
            new System.Runtime.CompilerServices.ConditionalWeakTable<JumpingFish, StardewValley.Object>();

        public static void Postfix(FishPond __instance)
        {
            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);

            // Tự động đẩy phần thưởng tiếp theo ra khe output khi trống (khắc phục Automate rút từng vật phẩm)
            if (__instance.output.Value == null && data.PendingOutputs != null && data.PendingOutputs.Count > 0)
            {
                __instance.output.Value = data.PendingOutputs[0].CreateItem();
                data.PendingOutputs.RemoveAt(0);
            }

            if (__instance.currentOccupants.Value == 0) return;
            if (data.FishCounts.Count == 0) return;

            // Tạo danh sách ID cá hiện có trong ao theo số lượng
            List<string> fishIds = new List<string>();
            foreach (var entry in data.FishCounts)
            {
                for (int i = 0; i < entry.Value; i++)
                {
                    fishIds.Add(entry.Key);
                }
            }

            int index = 0;
            
            // Đồng bộ bóng cá bơi
            var silhouettes = __instance._fishSilhouettes;
            if (silhouettes != null)
            {
                for (int i = 0; i < silhouettes.Count && index < fishIds.Count; i++, index++)
                {
                    var silhouette = silhouettes[i];
                    var field = typeof(PondFishSilhouette).GetField("_fishObject", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        var fishObj = field.GetValue(silhouette) as StardewValley.Object;
                        if (fishObj == null || fishObj.ItemId != fishIds[index])
                        {
                            var cachedItem = ModEntry.GetCachedFishItem(fishIds[index]) as StardewValley.Object;
                            if (cachedItem != null)
                            {
                                field.SetValue(silhouette, cachedItem);
                            }
                        }
                    }
                }
            }

            // Đồng bộ cá nhảy
            var jumping = __instance._jumpingFish;
            if (jumping != null && fishIds.Count > 0)
            {
                foreach (var jf in jumping)
                {
                    var field = typeof(JumpingFish).GetField("_fishObject", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        if (!jumpingFishMap.TryGetValue(jf, out var assignedFish))
                        {
                            // Get the list of species currently assigned to other active jumping fish
                            HashSet<string> activeSpecies = new HashSet<string>();
                            foreach (var activeJf in jumping)
                            {
                                if (jumpingFishMap.TryGetValue(activeJf, out var otherFish) && otherFish != null)
                                {
                                    activeSpecies.Add(otherFish.ItemId);
                                }
                            }

                            // Find the list of species in the pond that are NOT currently jumping
                            List<string> availableSpecies = new List<string>();
                            foreach (var entry in data.FishCounts.Keys)
                            {
                                if (!activeSpecies.Contains(entry))
                                {
                                    availableSpecies.Add(entry);
                                }
                            }

                            // If all species are already jumping, fall back to all species in the pond
                            if (availableSpecies.Count == 0)
                            {
                                availableSpecies.AddRange(data.FishCounts.Keys);
                            }

                            string chosenFishId = Game1.random.ChooseFrom(availableSpecies);
                            assignedFish = ModEntry.GetCachedFishItem(chosenFishId) as StardewValley.Object;
                            if (assignedFish != null)
                            {
                                jumpingFishMap.Add(jf, assignedFish);
                            }
                        }

                        if (assignedFish != null)
                        {
                            var currentObj = field.GetValue(jf) as StardewValley.Object;
                            if (currentObj == null || currentObj.ItemId != assignedFish.ItemId)
                            {
                                field.SetValue(jf, assignedFish);
                            }
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(FishPond), nameof(FishPond.ClearPond))]
    public class FishPond_ClearPond_Patch
    {
        public static bool Prefix(FishPond __instance)
        {
            Rectangle r = __instance.GetBoundingBox();
            PondData data = ModEntry.Instance.GetOrCreatePondData(__instance);

            foreach (var entry in data.FishCounts)
            {
                string fishId = entry.Key;
                int count = entry.Value;

                for (int i = 0; i < count; i++)
                {
                    Vector2 pos = Utility.PointToVector2(r.Center);
                    int direction = Game1.random.Next(4);
                    switch (direction)
                    {
                        case 0:
                            pos = new Vector2(Game1.random.Next(r.Left, r.Right), r.Top);
                            break;
                        case 1:
                            pos = new Vector2(r.Right, Game1.random.Next(r.Top, r.Bottom));
                            break;
                        case 2:
                            pos = new Vector2(Game1.random.Next(r.Left, r.Right), r.Bottom);
                            break;
                        case 3:
                            pos = new Vector2(r.Left, Game1.random.Next(r.Top, r.Bottom));
                            break;
                    }
                    Item fishInstance = new StardewValley.Object(fishId, 1);
                    Game1.createItemDebris(fishInstance, pos, direction, Game1.currentLocation, -1, flopFish: true);
                }
            }

            __instance.GetType().GetField("_hasAnimatedSpawnedFish", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(__instance, false);
            __instance.hasSpawnedFish.Value = false;
            __instance._fishSilhouettes.Clear();
            __instance._jumpingFish.Clear();
            __instance.goldenAnimalCracker.Value = false;
            __instance.isPlayingGoldenCrackerAnimation.Value = false;
            __instance.GetType().GetField("_fishObject", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(__instance, null);
            __instance.currentOccupants.Value = 0;
            __instance.daysSinceSpawn.Value = 0;
            __instance.neededItem.Value = null;
            __instance.neededItemCount.Value = -1;
            __instance.lastUnlockedPopulationGate.Value = 0;
            __instance.fishType.Value = null;
            __instance.Reseed();
            __instance.overrideWaterColor.Value = Color.White;

            data.FishCounts.Clear();
            data.LastUnlockedGates.Clear();
            data.DaysSinceSpawn.Clear();
            data.NeededItems.Clear();
            data.NeededItemCounts.Clear();
            data.PendingOutputs.Clear();

            return false;
        }
    }

    public class InvisibleFishObject : StardewValley.Object
    {
        private string _customDisplayName;
        private PondQueryMenu _menu;
        private PondData _data;

        public InvisibleFishObject(string itemId, string displayName, PondQueryMenu menu, PondData data) : base(itemId, 1)
        {
            this._customDisplayName = displayName;
            this._menu = menu;
            this._data = data;
        }

        public override string DisplayName
        {
            get => this._customDisplayName;
        }

        public override void drawInMenu(SpriteBatch spriteBatch, Vector2 location, float scaleSize, float transparency, float layerDepth, StackDrawType drawStackNumber, Color color, bool drawShadow)
        {
            if (_menu == null || _data == null || _data.FishCounts.Count == 0) return;

            int maxIconsPerRow = 5;
            int spacing = 56;
            int iconSize = 48;
            int rowHeight = 54;
            int startY = _menu.yPositionOnScreen + 275;

            // Split into rows of 5 icons
            List<List<KeyValuePair<string, int>>> rows = new List<List<KeyValuePair<string, int>>>();
            List<KeyValuePair<string, int>> currentRow = new List<KeyValuePair<string, int>>();
            foreach (var entry in _data.FishCounts)
            {
                currentRow.Add(entry);
                if (currentRow.Count == maxIconsPerRow)
                {
                    rows.Add(currentRow);
                    currentRow = new List<KeyValuePair<string, int>>();
                }
            }
            if (currentRow.Count > 0)
            {
                rows.Add(currentRow);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int nRow = row.Count;
                int rowWidth = (nRow - 1) * spacing + iconSize;
                int startX = _menu.xPositionOnScreen + ((IClickableMenu)_menu).width / 2 - rowWidth / 2;

                for (int i = 0; i < nRow; i++)
                {
                    var entry = row[i];
                    string fishId = entry.Key;
                    int count = entry.Value;

                    int x = startX + i * spacing;
                    int y = startY + r * rowHeight;

                    Item fishItem = ModEntry.GetCachedFishItem(fishId);
                    if (fishItem != null)
                    {
                        fishItem.drawInMenu(spriteBatch, new Vector2(x, y), 0.75f, 1f, 0.9f, StackDrawType.Hide, Color.White, true);
                    }

                    Utility.drawTinyDigits(count, spriteBatch, new Vector2(x + 32, y + 32), 2.0f, 1f, Color.White);
                }
            }
        }
    }
}