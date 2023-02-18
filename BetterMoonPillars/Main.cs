using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace BetterMoonPillars
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "prodzpod";
        public const string PluginName = "BetterMoonPillars";
        public const string PluginVersion = "1.0.1";
        public static ManualLogSource Log;
        internal static PluginInfo pluginInfo;
        public static ConfigFile Config;
        public static ConfigEntry<string> RewardList;
        public static ConfigEntry<int> RewardNumber;
        public static ConfigEntry<bool> RewardPlayerScale;
        public static ConfigEntry<int> RequiredPillars;
        public static ConfigEntry<bool> RewardPastRequired;
        public static ConfigEntry<float> RewardTime;
        public static ConfigEntry<float> DesignPillarMinRadius;
        public static ConfigEntry<float> PillarMinRadius;
        public static Dictionary<string, WeightedSelection<PickupIndex>> ItemSelections = new();
        public static WeightedSelection<PickupIndex> EquipmentSelections = new();
        public static WeightedSelection<PickupIndex> LunarEquipmentSelections = new();
        public static GameObject prefab;

        public void Awake()
        {
            pluginInfo = Info;
            Log = Logger;
            Config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, PluginGUID + ".cfg"), true);            
            RewardList = Config.Bind("General", "Reward List", "LunarTierDef, Tier2Def, Tier2Def", "List of tier names to select from, separated by commas. See available list in logs.");            
            RewardNumber = Config.Bind("General", "Reward Amount", 1, "Number of items to reward per pillar.");            
            RewardPlayerScale = Config.Bind("General", "Reward Player Scale", false, "Whether the reward number should scale to player count.");            
            RequiredPillars = Config.Bind("General", "Required Pillars", 0, "How many pillars are required.");            
            RewardPastRequired = Config.Bind("General", "Only Reward Past Required", false, "Whether all or only the extra pillars should reward players.");
            RewardTime = Config.Bind("General", "Reward Time", 0f, "Amount of time rewinded per pillar in seconds.");
            DesignPillarMinRadius = Config.Bind("General", "Pillar of Design Minimum Radius", 10f, "Minimum Pillar of Design radius for Focused Convergence.");
            PillarMinRadius = Config.Bind("General", "Other Pillars Minimum Radius", 7f, "Minimum pillar radius for Focused Convergence.");
            RoR2Application.onLoad += PostStart;
        }

        public void PostStart()
        {
            Log.LogInfo("Available Tiers: " + ItemTierCatalog.allItemTierDefs.Join(x => x.name) + ", EquipmentTierDef, LunarEquipmentTierDef");
            if (RewardList.Value != "") PatchReward();
            if (RewardTime.Value != 0) On.EntityStates.Missions.Moon.MoonBatteryComplete.OnEnter += (orig, self) => 
            { 
                orig(self);
                if (RewardPastRequired.Value && MoonBatteryMissionController.instance != null && MoonBatteryMissionController.instance.numChargedBatteries <= MoonBatteryMissionController.instance.numRequiredBatteries) return;
                Run.instance.SetRunStopwatch(Mathf.Max(0, Run.instance.GetRunStopwatch() - RewardTime.Value)); 
            };
            if (RequiredPillars.Value <= 0) PatchOptional();
            else
            {
                if (RequiredPillars.Value != 4) On.RoR2.MoonBatteryMissionController.OnEnable += (orig, self) => { self._numRequiredBatteries = RequiredPillars.Value; orig(self); };
                if (RewardPastRequired.Value) On.RoR2.MoonBatteryMissionController.OnBatteryCharged += (orig, self, zone) =>
                {
                    if (!NetworkServer.active || self.numChargedBatteries + 1 < self.numRequiredBatteries) orig(self, zone);
                    else
                    {
                        self.Network_numChargedBatteries = self.numChargedBatteries + 1;
                        if (self.numChargedBatteries + 1 == self.numRequiredBatteries) for (int index = 0; index < self.elevatorStateMachines.Length; ++index)
                                self.elevatorStateMachines[index].SetNextState(new EntityStates.MoonElevator.InactiveToReady());
                    }
                };
            }
            if (PillarMinRadius.Value > 0) On.RoR2.HoldoutZoneController.OnEnable += (orig, self) =>
            {
                if (self.gameObject.name.Contains("MoonBatteryDesign")) self.minimumRadius = DesignPillarMinRadius.Value;
                else if (self.gameObject.name.Contains("MoonBattery")) self.minimumRadius = PillarMinRadius.Value;
                orig(self); 
            };
        }

        public static void PatchReward()
        {
            List<string> list = RewardList.Value.Split(',').ToList().ConvertAll(x => x.Trim());
            prefab = Addressables.LoadAssetAsync<GameObject>("RoR2/DLC1/OptionPickup/OptionPickup.prefab").WaitForCompletion();
            Run.onRunStartGlobal += (run) =>
            {
                ItemSelections.Clear();
                EquipmentSelections.Clear();
                LunarEquipmentSelections.Clear();
                foreach (var q in ItemCatalog.allItemDefs)
                {
                    ItemTierDef tier = ItemTierCatalog.GetItemTierDef(q.tier);
                    if (tier?.name == null || !run.IsPickupAvailable(PickupCatalog.FindPickupIndex(q.itemIndex))) continue;
                    if (!ItemSelections.ContainsKey(tier.name)) ItemSelections.Add(tier.name, new());
                    ItemSelections[tier.name].AddChoice(new() { value = PickupCatalog.FindPickupIndex(q.itemIndex), weight = 1 });
                }
                foreach (var q in EquipmentCatalog.equipmentDefs) if (run.IsPickupAvailable(PickupCatalog.FindPickupIndex(q.equipmentIndex))) 
                        (q.isLunar ? LunarEquipmentSelections : EquipmentSelections).AddChoice(new() { value = PickupCatalog.FindPickupIndex(q.equipmentIndex), weight = 1 });
            };
            On.EntityStates.Missions.Moon.MoonBatteryComplete.OnEnter += (orig, self) =>
            {
                orig(self);
                if (RewardPastRequired.Value && MoonBatteryMissionController.instance != null && MoonBatteryMissionController.instance.numChargedBatteries <= MoonBatteryMissionController.instance.numRequiredBatteries) return;
                List<List<PickupIndex>> options = new();
                int num = RewardNumber.Value * (RewardPlayerScale.Value ? Run.instance.participatingPlayerCount : 1);
                for (int i = 0; i < num; i++)
                {
                    List<PickupIndex> option = new();
                    foreach (var tier in list) option.Add(GetPickupFromTier(tier));
                    options.Add(option);
                }
                Vector3 position = self.gameObject.transform.position + (Vector3.up * 4);
                Vector3 velocity = Vector3.up * 10 + self.gameObject.transform.forward * 20;
                Quaternion quaternion = Quaternion.AngleAxis(360f / num, Vector3.up);
                for (int i = 0; i < num; i++)
                {
                    List<PickupIndex> option = options[i];
                    if (option.Count > 1) PickupDropletController.CreatePickupDroplet(new GenericPickupController.CreatePickupInfo()
                    {
                        pickerOptions = PickupPickerController.GenerateOptionsFromArray(option.ToArray()),
                        prefabOverride = prefab,
                        position = self.gameObject.transform.position,
                        rotation = Quaternion.identity,
                        pickupIndex = option[0]
                    }, position, velocity);
                    else PickupDropletController.CreatePickupDroplet(option[0], position, velocity);
                    velocity = quaternion * velocity;
                }
            };
        }

        public static PickupIndex GetPickupFromTier(string tier)
        {
            if (tier == "EquipmentTierDef") return PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, EquipmentSelections)[0];
            if (tier == "LunarEquipmentTierDef") return PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, LunarEquipmentSelections)[0];
            return PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, ItemSelections[tier] ?? ItemSelections.Values.First())[0];
        }

        public static void PatchOptional()
        {
            IL.RoR2.MoonBatteryMissionController.OnEnable += (il) =>
            {
                ILCursor c = new(il);
                c.GotoNext(x => x.MatchLdftn<MoonBatteryMissionController>(nameof(MoonBatteryMissionController.OnCollectObjectiveSources)));
                c.Emit(OpCodes.Pop);
                while (c.Next.OpCode != OpCodes.Ret) c.Remove();
            };
            IL.RoR2.MoonBatteryMissionController.OnDisable += (il) =>
            {
                ILCursor c = new(il);
                c.GotoNext(x => x.MatchLdftn<MoonBatteryMissionController>(nameof(MoonBatteryMissionController.OnCollectObjectiveSources)));
                c.Emit(OpCodes.Pop);
                while (c.Next.OpCode != OpCodes.Ret) c.Remove();
            };
            Stage.onStageStartGlobal += (self) =>
            {
                foreach (var obj in FindObjectsOfType<GameObject>()) if (obj.name.Contains("MoonElevator"))
                {
                    EntityStateMachine esm = obj.GetComponent<EntityStateMachine>();
                    if (esm != null) esm.SetState(new EntityStates.MoonElevator.InactiveToReady());
                }
            };
        }
    }
}
