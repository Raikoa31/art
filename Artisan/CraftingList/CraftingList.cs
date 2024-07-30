﻿using Artisan.Autocraft;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.GenericHelpers;
using ECommons.Automation.UIInput;

namespace Artisan.CraftingLists
{
    public class CraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<uint> Items { get; set; } = new();

        public Dictionary<uint, ListItemOptions> ListItemOptions { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class NewCraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<ListItem> Recipes { get; set; } = new();

        public List<uint> ExpandedList { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class ListItem
    {
        public uint ID { get; set; }

        public int Quantity { get; set; }

        public ListItemOptions? ListItemOptions { get; set; } = new();

    }

    public class ListItemOptions
    {
        public bool NQOnly { get; set; }
        // TODO: custom RecipeConfig?

        public bool Skipping { get; set; }
    }

    public static class CraftingListFunctions
    {
        public static int CurrentIndex;

        public static bool Paused { get; set; } = false;

        public static Dictionary<uint, int>? Materials;

        public static TaskManager CLTM = new();

        public static TimeSpan ListEndTime = default(TimeSpan);

        public static void SetID(this NewCraftingList list)
        {
            var rng = new Random();
            var proposedRNG = rng.Next(1, 50000);
            while (P.Config.NewCraftingLists.Where(x => x.ID == proposedRNG).Any())
            {
                proposedRNG = rng.Next(1, 50000);
            }

            list.ID = proposedRNG;
        }

        public static Dictionary<uint, int> ListMaterials(this NewCraftingList list)
        {
            var output = new Dictionary<uint, int>();
            foreach (var item in list.Recipes)
            {
                if (item.ListItemOptions == null)
                {
                    item.ListItemOptions = new ListItemOptions();
                    P.Config.Save();
                }
                if (item.ListItemOptions.Skipping || item.Quantity == 0) continue;
                Recipe r = LuminaSheets.RecipeSheet[item.ID];
                CraftingListHelpers.AddRecipeIngredientsToList(r, ref output, false, list);
            }

            return output;
        }

        public static bool Save(this NewCraftingList list, bool isNew = false)
        {
            if (list.Recipes.Count == 0 && !isNew) return false;

            list.SkipIfEnough = P.Config.DefaultListSkip;
            list.Materia = P.Config.DefaultListMateria;
            list.Repair = P.Config.DefaultListRepair;
            list.RepairPercent = P.Config.DefaultListRepairPercent;
            list.AddAsQuickSynth = P.Config.DefaultListQuickSynth;

            if (list.AddAsQuickSynth)
            {
                foreach (var item in list.Recipes)
                {
                    if (item.ListItemOptions == null)
                    {
                        item.ListItemOptions = new ListItemOptions();
                    }
                    item.ListItemOptions.NQOnly = true;
                }
            }

            P.Config.NewCraftingLists.Add(list);
            P.Config.Save();
            return true;
        }

        public static unsafe bool RecipeWindowOpen()
        {
            return TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) && addon->AtkUnitBase.IsVisible && Operations.GetSelectedRecipeEntry() != null;
        }

        public static unsafe void OpenRecipeByID(uint recipeID, bool skipThrottle = false)
        {
            if (Crafting.CurState != Crafting.State.IdleNormal) return;

            var re = Operations.GetSelectedRecipeEntry();

            if (!TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) || (re != null && re->RecipeId != recipeID))
            {
                AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID);
            }
        }

        public static bool HasItemsForRecipe(uint currentProcessedItem)
        {
            if (currentProcessedItem == 0) return false;
            var recipe = LuminaSheets.RecipeSheet[currentProcessedItem];
            if (recipe.RowId == 0) return false;

            return CraftingListUI.CheckForIngredients(recipe, false);
        }

        internal static unsafe void ProcessList(NewCraftingList selectedList)
        {
            var isCrafting = Svc.Condition[ConditionFlag.Crafting];
            var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
            Materials ??= selectedList.ListMaterials();

            if (Paused)
            {
                return;
            }

            if (CurrentIndex < selectedList.ExpandedList.Count)
            {
                CraftingListUI.CurrentProcessedItem = selectedList.ExpandedList[CurrentIndex];
            }
            else
            {
                Svc.Log.Verbose("End of Index");
                CurrentIndex = 0;
                CraftingListUI.Processing = false;
                Operations.CloseQuickSynthWindow();
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromSeconds(5)));

                if (P.Config.PlaySoundFinishList)
                    Sounds.SoundPlayer.PlaySound();
                return;
            }

            var recipe = LuminaSheets.RecipeSheet[CraftingListUI.CurrentProcessedItem];
            var options = selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions;
            var config = /* options?.CustomConfig ?? */ P.Config.RecipeConfigs.GetValueOrDefault(CraftingListUI.CurrentProcessedItem) ?? new();
            var needToRepair = selectedList.Repair && RepairManager.GetMinEquippedPercent() < selectedList.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            PreCrafting.CraftType type = (options?.NQOnly ?? false) && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;

            if (Crafting.QuickSynthState.Max > 0 && (needToRepair || Crafting.QuickSynthCompleted || selectedList.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (PreCrafting.Tasks.Count > 0 || Crafting.CurState is not Crafting.State.IdleNormal and not Crafting.State.IdleBetween and not Crafting.State.InvalidState)
            {
                return;
            }

            if (recipe.SecretRecipeBook.Row != 0)
            {
                if (!PlayerState.Instance()->IsSecretRecipeBookUnlocked(recipe.SecretRecipeBook.Row))
                {
                    SeString error = new SeString(
                        new TextPayload("You haven't unlocked the recipe book "),
                        new ItemPayload(recipe.SecretRecipeBook.Value.Item.Row),
                        new UIForegroundPayload(1),
                        new TextPayload(recipe.SecretRecipeBook.Value.Name.RawString),
                        RawPayload.LinkTerminator,
                        UIForegroundPayload.UIForegroundOff,
                        new TextPayload(" for this recipe. Moving on."));

                    Svc.Chat.Print(new Dalamud.Game.Text.XivChatEntry()
                    {
                        Message = error,
                        Type = Dalamud.Game.Text.XivChatType.ErrorMessage,
                    });

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }
                }
            }

            if (selectedList.SkipIfEnough && (preparing || !isCrafting))
            {
                var ItemId = recipe.ItemResult.Row;
                int numMats = Materials.Any(x => x.Key == recipe.ItemResult.Row) && !selectedList.SkipLiteral ? Materials.First(x => x.Key == recipe.ItemResult.Row).Value : selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Row == ItemId) * recipe.AmountResult;
                if (numMats <= CraftingListUI.NumberOfIngredient(recipe.ItemResult.Row))
                {
                    DuoLog.Error($"Skipping {recipe.ItemResult.Value.Name} due to having enough in inventory [Skip Items you already have enough of]");

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }

                    return;
                }
            }

            if (!HasItemsForRecipe(CraftingListUI.CurrentProcessedItem) && (preparing || !isCrafting))
            {
                DuoLog.Error($"Insufficient materials for {recipe.ItemResult.Value.Name.ExtractText()}. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (Svc.ClientState.LocalPlayer.ClassJob.Id != recipe.CraftType.Value.RowId + 8)
            {
                PreCrafting.equipGearsetLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)recipe.CraftType.Value.RowId + 8), TimeSpan.FromMilliseconds(200)));

                return;
            }

            bool needEquipItem = recipe.ItemRequired.Row > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.Row);
            if (needEquipItem)
            {
                PreCrafting.equipAttemptLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.Row), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (Svc.ClientState.LocalPlayer.Level < recipe.RecipeLevelTable.Value.ClassJobLevel - 5 && Svc.ClientState.LocalPlayer.ClassJob.Id == recipe.CraftType.Value.RowId + 8 && !isCrafting && !preparing)
            {
                DuoLog.Error("Insufficient level to craft this item. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (!Spiritbond.ExtractMateriaTask(selectedList.Materia))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (selectedList.Repair && !RepairManager.ProcessRepair(selectedList))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions is null)
            {
                selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions = new ListItemOptions();
            }
            bool needConsumables = (type == PreCrafting.CraftType.Normal || (type == PreCrafting.CraftType.Quick && P.Config.UseConsumablesQuickSynth)) && (!ConsumableChecker.IsFooded(config) || !ConsumableChecker.IsPotted(config) || !ConsumableChecker.IsManualled(config) || !ConsumableChecker.IsSquadronManualled(config));
            bool hasConsumables = config != default ? ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && ConsumableChecker.HasItem(config.RequiredManual, false) && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) : true;

            if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
            {
                DuoLog.Error($"Can't craft {recipe.ItemResult.Value?.Name}: required consumables not up");
                Paused = true;
                return;
            }

            if (needConsumables && hasConsumables)
            {
                if (!CLTM.IsBusy && !PreCrafting.Occupied())
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                    CLTM.DelayNext(100);
                }
                return;
            }

            if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
            {
                if (!CLTM.IsBusy)
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(200))));

                    if (!RecipeWindowOpen()) return;

                    if (type == PreCrafting.CraftType.Quick)
                    {
                        var lastIndex = selectedList.ExpandedList.LastIndexOf(CraftingListUI.CurrentProcessedItem);
                        var count = lastIndex - CurrentIndex + 1;
                        count = CheckWhatExpected(selectedList, recipe, count);
                        if (count >= 99)
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(99));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQS99WaitStart");
                            return;
                        }
                        else
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(count));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQSCountWaitStart");
                            return;
                        }
                    }
                    else if (type == PreCrafting.CraftType.Normal)
                    {
                        CLTM.DelayNext((int)(Math.Min(P.Config.ListCraftThrottle2, 2) * 1000));
                        CLTM.Enqueue(() => SetIngredients(), "SettingIngredients");
                        CLTM.Enqueue(() => Operations.RepeatActualCraft(), "ListCraft");
                        CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListNormalWaitStart");
                        return;

                    }
                }

            }
        }

        private static int CheckWhatExpected(NewCraftingList selectedList, Recipe recipe, int count)
        {
            if (selectedList.SkipIfEnough)
            {
                var inventoryitems = CraftingListUI.NumberOfIngredient(recipe.ItemResult.Value.RowId);
                var expectedNumber = 0;
                var stillToCraft = 0;
                var totalToCraft = selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Value.Name.RawString == recipe.ItemResult.Value.Name.RawString) * recipe.AmountResult;
                if (Materials!.Count(x => x.Key == recipe.ItemResult.Row) == 0 || selectedList.SkipLiteral)
                {
                    // var previousCrafted = selectedList.Items.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Value.Name.RawString == recipe.ItemResult.Value.Name.RawString && selectedList.Items.IndexOf(x) < CurrentIndex) * recipe.AmountResult;
                    stillToCraft = selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Row == recipe.ItemResult.Row && selectedList.ExpandedList.IndexOf(x) >= CurrentIndex) * recipe.AmountResult - inventoryitems;
                    expectedNumber = stillToCraft > 0 ? Math.Min(selectedList.ExpandedList.Count(x => x == CraftingListUI.CurrentProcessedItem) * recipe.AmountResult, stillToCraft) : selectedList.ExpandedList.Count(x => x == CraftingListUI.CurrentProcessedItem);
                }
                else
                {
                    expectedNumber = Materials!.First(x => x.Key == recipe.ItemResult.Row).Value;
                }

                var difference = Math.Min(totalToCraft - inventoryitems, expectedNumber);
                Svc.Log.Debug($"{recipe.ItemResult.Value.Name} {expectedNumber} {difference}");
                double numberToCraft = Math.Ceiling((double)difference / recipe.AmountResult);

                count = (int)numberToCraft;
            }

            return count;
        }

        public static unsafe bool SetIngredients(EnduranceIngredients[]? setIngredients = null)
        {
            var recipe = Operations.GetSelectedRecipeEntry();
            if (recipe == null)
                return false;

            if (TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) &&
                addon->AtkUnitBase.IsVisible &&
                AgentRecipeNote.Instance() != null &&
                RaptureAtkModule.Instance()->AtkModule.IsAddonReady(AgentRecipeNote.Instance()->AgentInterface.AddonId))
            {
                if (setIngredients == null)
                {
                    for (uint i = 0; i <= 5; i++)
                    {
                        try
                        {
                            var node = addon->AtkUnitBase.UldManager.NodeList[23 - i]->GetAsAtkComponentNode();

                            if (node is null || !node->AtkResNode.IsVisible())
                            {
                                continue;
                            }

                            if (node->Component->UldManager.NodeList[11]->IsVisible())
                            {
                                var ingredient = LuminaSheets.RecipeSheet.Values.Where(x => x.RowId == Endurance.RecipeID).FirstOrDefault().UnkData5[i].ItemIngredient;

                                var btn = node->Component->UldManager.NodeList[14]->GetAsAtkComponentButton();
                                try
                                {
                                    btn->ClickAddonButton((AtkComponentBase*)addon, 4, EventType.CHANGE);
                                }
                                catch (Exception ex)
                                {
                                    ex.Log();
                                }
                                var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextIconMenu");
                                if (contextMenu != null)
                                {
                                    Callback.Fire(contextMenu, true, 0, 0, 0, ingredient, 0);
                                }
                            }
                            else
                            {
                                for (int m = 0; m <= 100; m++)
                                {
                                   new AddonMaster.RecipeNote((IntPtr)addon).Material(i, false);
                                }

                                for (int m = 0; m <= 100; m++)
                                {
                                   new AddonMaster.RecipeNote((IntPtr)addon).Material(i, true);
                                }
                            }

                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    for (uint i = 0; i <= 5; i++)
                    {
                        try
                        {
                            var node = addon->AtkUnitBase.UldManager.NodeList[23 - i]->GetAsAtkComponentNode();
                            if (node->Component->UldManager.NodeListCount < 16)
                                return false;

                            if (node is null || !node->AtkResNode.IsVisible())
                            {
                                continue;
                            }

                            var hqSetButton = node->Component->UldManager.NodeList[6]->GetAsAtkComponentNode();
                            var nqSetButton = node->Component->UldManager.NodeList[9]->GetAsAtkComponentNode();

                            var hqSetText = hqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;
                            var nqSetText = nqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;

                            int hqSet = Convert.ToInt32(hqSetText.ToString().GetNumbers());
                            int nqSet = Convert.ToInt32(nqSetText.ToString().GetNumbers());

                            if (setIngredients.Any(y => y.IngredientSlot == i))
                            {
                                for (int h = hqSet; h < setIngredients.First(x => x.IngredientSlot == i).HQSet; h++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material(i, true);
                                }

                                for (int h = nqSet; h < setIngredients.First(x => x.IngredientSlot == i).NQSet; h++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material(i, false);
                                }
                            }
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
            }
            else
            {
                return false;
            }

            return true;
        }
    }
}
