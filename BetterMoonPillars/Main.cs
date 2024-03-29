﻿using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using System;
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
        public const string PluginVersion = "1.2.1";
        public static ManualLogSource Log;
        internal static PluginInfo pluginInfo;
        public static ConfigFile Config;
        public static ConfigEntry<string> RewardList;
        public static ConfigEntry<int> RewardNumber;
        public static ConfigEntry<bool> RewardPlayerScale;
        public static ConfigEntry<int> RequiredPillars;
        public static ConfigEntry<bool> RewardPastRequired;
        public static ConfigEntry<float> RewardTime;
        public static ConfigEntry<bool> RewardIsPotential;
        public static ConfigEntry<float> DesignPillarMinRadius;
        public static ConfigEntry<float> PillarMinRadius;
        public static ConfigEntry<int> PillarExtraLunar;
        public static ConfigEntry<float> PillarHealth;
        public static ConfigEntry<float> PillarDamage;
        public static ConfigEntry<float> PillarSpeed;
        public static ConfigEntry<float> PillarArmor;
        public static ConfigEntry<float> PillarAttackSpeed;
        public static Dictionary<string, WeightedSelection<PickupIndex>> ItemSelections = new();
        public static WeightedSelection<PickupIndex> EquipmentSelections = new();
        public static WeightedSelection<PickupIndex> LunarEquipmentSelections = new();
        public static GameObject prefab;

        public static int pillarCompleted = 0;
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
            RewardIsPotential = Config.Bind("General", "Reward is Potential", true, "Whether to drop reward pickup as a potential or an item randomly chosen from the union of above pool.");
            DesignPillarMinRadius = Config.Bind("General", "Pillar of Design Minimum Radius", 10f, "Minimum Pillar of Design radius for Focused Convergence.");
            PillarMinRadius = Config.Bind("General", "Other Pillars Minimum Radius", 7f, "Minimum pillar radius for Focused Convergence.");
            PillarExtraLunar = Config.Bind("General", "Extra Lunar Coin per Pillar", 0, "Extra lunar coin reward for each pillar.");
            PillarHealth = Config.Bind("General", "Mithrix Health Multiplier per Pillar", 0f, "Mithrix gets stronger/weaker each pillar. Multiplicative.");
            PillarDamage = Config.Bind("General", "Mithrix Damage Multiplier per Pillar", 0f, "Mithrix gets stronger/weaker each pillar. Multiplicative.");
            PillarSpeed = Config.Bind("General", "Mithrix Speed Bonus per Pillar", 0f, "Mithrix gets stronger/weaker each pillar. Multiplicative.");
            PillarArmor = Config.Bind("General", "Mithrix Armor Bonus per Pillar", 0f, "Mithrix gets stronger/weaker each pillar. Multiplicative.");
            PillarAttackSpeed = Config.Bind("General", "Mithrix Damage Multiplier per Pillar", 0f, "Mithrix gets stronger/weaker each pillar. Multiplicative.");
            RoR2Application.onLoad += PostStart;
        }

        public void PostStart()
        {
            Run.onRunStartGlobal += _ => pillarCompleted = 0;
            On.RoR2.ScriptedCombatEncounter.BeginEncounter += (orig, self) =>
            {
                orig(self);
                if (SceneCatalog.mostRecentSceneDef.cachedName.StartsWith("moon"))
                {
                    foreach (var member in self.combatSquad.membersList)
                    {
                        CharacterBody body = member.GetBody();
                        body.baseMaxHealth *= 1 + (PillarHealth.Value * pillarCompleted);
                        body.levelMaxHealth *= 1 + (PillarHealth.Value * pillarCompleted);
                        body.baseDamage *= 1 + (PillarDamage.Value * pillarCompleted);
                        body.levelDamage *= 1 + (PillarDamage.Value * pillarCompleted);
                        body.baseMoveSpeed *= 1 + (PillarSpeed.Value * pillarCompleted);
                        body.levelMoveSpeed *= 1 + (PillarSpeed.Value * pillarCompleted);
                        body.baseArmor *= 1 + (PillarArmor.Value * pillarCompleted);
                        body.levelArmor *= 1 + (PillarArmor.Value * pillarCompleted);
                        body.baseAttackSpeed *= 1 + (PillarAttackSpeed.Value * pillarCompleted);
                        body.levelAttackSpeed *= 1 + (PillarAttackSpeed.Value * pillarCompleted);
                    }
                }
            };
            IL.RoR2.Run.BeginGameOver += (il) =>
            {
                ILCursor c = new(il);
                c.GotoNext(x => x.MatchCallOrCallvirt<NetworkUser>(nameof(NetworkUser.AwardLunarCoins)));
                c.EmitDelegate<Func<uint, uint>>(orig => (uint)(orig + (pillarCompleted * PillarExtraLunar.Value)));
            };
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
                pillarCompleted++;
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
                    if (RewardIsPotential.Value && option.Count > 1) PickupDropletController.CreatePickupDroplet(new GenericPickupController.CreatePickupInfo()
                    {
                        pickerOptions = PickupPickerController.GenerateOptionsFromArray(option.ToArray()),
                        prefabOverride = prefab,
                        position = self.gameObject.transform.position,
                        rotation = Quaternion.identity,
                        pickupIndex = option[0]
                    }, position, velocity);
                    else PickupDropletController.CreatePickupDroplet(Run.instance.treasureRng.NextElementUniform(option), position, velocity);
                    velocity = quaternion * velocity;
                }
            };
        }

        public static PickupIndex GetPickupFromTier(string tier)
        {
            PickupIndex result = PickupIndex.none;
            if (tier == "EquipmentTierDef") result = PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, EquipmentSelections)[0];
            else if (tier == "LunarEquipmentTierDef") result = PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, LunarEquipmentSelections)[0];
            else if (ItemSelections.ContainsKey(tier)) result = PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, ItemSelections[tier])[0];
            else
            {
                foreach (ItemDef itemDef in ItemCatalog.allItemDefs) if (itemDef.name == tier && itemDef.itemIndex != ItemIndex.None)
                {
                    PickupIndex pickupIndex = PickupCatalog.FindPickupIndex(itemDef.itemIndex);
                    if (pickupIndex != PickupIndex.none) return pickupIndex;
                }
                foreach (EquipmentDef equipmentDef in EquipmentCatalog.equipmentDefs) if (equipmentDef.name == tier && equipmentDef.equipmentIndex != EquipmentIndex.None)
                {
                    PickupIndex pickupIndex2 = PickupCatalog.FindPickupIndex(equipmentDef.equipmentIndex);
                    if (pickupIndex2 != PickupIndex.none) return pickupIndex2;
                }
                result = PickupDropTable.GenerateUniqueDropsFromWeightedSelection(1, Run.instance.treasureRng, ItemSelections.Values.First())[0];
            }
            return result;
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
