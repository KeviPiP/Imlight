/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * CRAFTING SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Handles the crafting (alchemy) mechanics: using recipes to craft items,
 * equipping/unequipping recipes, and clearing crafting slots.
 *
 * NOTE:
 * Recipe templates are resolved via RecipeFactory (Recipes-WorldData.wad).
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Recipes;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal class CraftingService(SessionActor sessionActor) : MessageService(sessionActor), IWithTimers {

    // Generic non-zero error code returned to the client when a craft request is invalid.
    private const uint CRAFT_ERROR_GENERIC = 1;

    // The number of crafting slots a player has before any are granted by crafting
    // quests. Total concurrent timed-craft capacity is this plus AlchemyBehavior's
    // BonusCraftingSlots. A timed craft occupies one slot until its cook timer elapses.
    private const int BASE_CRAFTING_SLOTS = 1;

    public ITimerScheduler Timers { get; set; }

    // Internal scheduler message: fires when an occupied crafting slot's cook timer
    // elapses, freeing the slot so the player can craft again.
    private sealed record FreeCraftingSlot(ulong SlotGlobalId);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CraftingService(parentActor));

    // When the player finishes attaching, resume crafting-slot cooldowns across a relog:
    // free any whose timer already elapsed, and reschedule the rest.
    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceivePostAttach(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            var alchemy = wizard?.AlchemyBehavior;
            if (alchemy?.CraftingSlots is null) {
                return;
            }

            // Tell the client how many bonus crafting slots the player has.
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTCOUNT {
                GlobalID = wizard.CharId,
                BonusSlots = alchemy.BonusCraftingSlots,
            });

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var slot in alchemy.CraftingSlots.ToList()) {
                var remaining = slot.m_timeFinished - now;
                if (remaining <= 0) {
                    FreeSlot(wizard, (ulong) slot.m_globalID);
                }
                else {
                    ScheduleSlotFree((ulong) slot.m_globalID, remaining);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("CraftingService post-attach failed: {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_USERECIPE))]
    private void ReceiveUseRecipe(WIZARD_12_PROTOCOL.MSG_USERECIPE message) {
        var wizard = GetActiveWizard();
        var recipeName = (string) message.RecipeName;

        var recipe = RecipeFactory.GetRecipeByName(recipeName);
        if (recipe is null) {
            Logger.Warning("Player {0} attempted to craft unknown recipe '{1}'.",
                Logger.Args(wizard.CharId, recipeName));

            SendCraftError(message);
            return;
        }

        // Validate that the player has all required reagent ingredients.
        if (recipe.m_ingredients is not null) {
            foreach (var ingredient in recipe.m_ingredients) {
                var reagentTemplateId = (ulong) ingredient.m_itemID;
                var owned = wizard.AlchemyBehavior.GetReagent(reagentTemplateId);
                if (owned is null || owned.m_quantity < ingredient.m_quantity) {
                    Logger.Debug("Player {0} is missing reagents for recipe '{1}'.",
                        Logger.Args(wizard.CharId, recipeName));

                    SendCraftError(message);
                    return;
                }
            }
        }

        // Validate that the player has enough gold.
        if (wizard.GameStats.m_currentGold < recipe.m_goldCost) {
            Logger.Debug("Player {0} lacks gold for recipe '{1}'.",
                Logger.Args(wizard.CharId, recipeName));

            SendCraftError(message);
            return;
        }

        // A timed recipe occupies one crafting slot for its cook duration. If the player
        // has no free slot, they must wait for an in-progress craft to finish first.
        var isTimedCraft = recipe.m_cookTime > 0;
        if (isTimedCraft && GetFreeCraftingSlots(wizard) <= 0) {
            Logger.Debug("Player {0} has no free crafting slot for recipe '{1}'.",
                Logger.Args(wizard.CharId, recipeName));

            SendCraftError(message);
            return;
        }

        // Consume the reagents.
        if (recipe.m_ingredients is not null) {
            foreach (var ingredient in recipe.m_ingredients) {
                var reagentTemplateId = (ulong) ingredient.m_itemID;
                var owned = wizard.AlchemyBehavior.GetReagent(reagentTemplateId);
                if (owned is null) {
                    continue;
                }

                for (var i = 0; i < ingredient.m_quantity; i++) {
                    wizard.RemoveReagent(owned.m_globalID, out _);
                }
            }
        }

        // Consume the gold.
        if (recipe.m_goldCost > 0) {
            wizard.RemoveGold(recipe.m_goldCost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch,
            });
        }

        // The crafted item is granted immediately on use (W101 crafting grants on craft,
        // not when a timer finishes).
        GrantCraftOutput(wizard, recipe);

        // A timed recipe additionally occupies one crafting slot for its cook duration;
        // the slot frees automatically when the timer elapses, gating how many timed
        // crafts the player can have in progress at once. Instant recipes use no slot.
        if (isTimedCraft) {
            var timeFinished = (int) (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + recipe.m_cookTime);
            var craftingSlot = new CraftingSlot {
                m_recipeName = recipeName,
                m_timeFinished = timeFinished,
            };
            wizard.AddCraftingSlot(craftingSlot);

            // Inform the client of the new (occupied) crafting slot.
            var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
            if (serializer.Serialize(craftingSlot, 1, out var slotData)) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTADD {
                    GlobalID = wizard.CharId,
                    Data = slotData,
                });
            }
            else {
                Logger.Error("Failed to serialize crafting slot for recipe '{0}'.", Logger.Args(recipeName));
            }

            // Free the slot when the cook timer elapses.
            ScheduleSlotFree((ulong) craftingSlot.m_globalID, recipe.m_cookTime);
        }

        // Acknowledge the craft to the client.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_USERECIPE {
            RecipeName = message.RecipeName,
            FinalItemID = recipe.m_itemID,
            Error = 0,
            Quantity = message.Quantity,
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE))]
    private void ReceiveEquipRecipe(WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE message) {
        var wizard = GetActiveWizard();

        if (message.Equip != 0) {
            // Add (equip) the recipe.
            wizard.AddRecipe(new Recipe { m_recipeNameID = message.RecipeID });
        }
        else {
            // Remove (unequip) the recipe.
            wizard.RemoveRecipe(message.RecipeID);
        }

        // Echo the request back to the client to confirm.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE {
            GlobalID = message.GlobalID,
            RecipeID = message.RecipeID,
            Equip = message.Equip,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_CLEARALLCRAFTINGSLOTS))]
    private void ReceiveClearAllCraftingSlots(WIZARD_12_PROTOCOL.MSG_CLEARALLCRAFTINGSLOTS message) {
        var wizard = GetActiveWizard();
        wizard.ClearCraftingSlots();

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_CLEARALLCRAFTINGSLOTS {
            GlobalID = wizard.CharId,
        });
    }

    // The number of crafting slots the player currently has available for a new timed
    // craft: their total capacity minus the slots already occupied by in-progress crafts.
    private static int GetFreeCraftingSlots(WizardData.Models.Player.Wizard wizard) {
        var alchemy = wizard.AlchemyBehavior;
        var maxSlots = BASE_CRAFTING_SLOTS + (alchemy?.BonusCraftingSlots ?? 0);
        var occupied = alchemy?.CraftingSlots?.Count ?? 0;

        return maxSlots - occupied;
    }

    private void ScheduleSlotFree(ulong slotGlobalId, long delaySeconds)
        => Timers.StartSingleTimer(
            $"craftslot-{slotGlobalId}",
            new FreeCraftingSlot(slotGlobalId),
            TimeSpan.FromSeconds(Math.Max(1, delaySeconds)));

    [MessageHandler(typeof(FreeCraftingSlot))]
    private void ReceiveFreeCraftingSlot(FreeCraftingSlot message)
        => FreeSlot(GetActiveWizard(), message.SlotGlobalId);

    // Releases a crafting slot whose cook timer has elapsed and tells the client it is
    // available again. The crafted item was already granted at craft time.
    private void FreeSlot(WizardData.Models.Player.Wizard wizard, ulong slotGlobalId) {
        var exists = wizard.AlchemyBehavior?.CraftingSlots?
            .Any(s => (ulong) s.m_globalID == slotGlobalId) ?? false;
        if (!exists) {
            return;
        }

        wizard.RemoveCraftingSlot(slotGlobalId);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTREMOVE {
            GlobalID = wizard.CharId,
            ItemID = slotGlobalId,
        });
    }

    private void GrantCraftOutput(WizardData.Models.Player.Wizard wizard, RecipeTemplate recipe) {
        // Prefer a fixed output item; fall back to rolling the recipe's loot table.
        if (recipe.m_itemID != 0) {
            wizard.AddItemToInventory((ulong) recipe.m_itemID, out _);

            return;
        }

        if (!string.IsNullOrEmpty(recipe.m_itemLootTable)) {
            var playerObject = GetActiveGameObject();
            var rollResults = DropTableRoller.Roll(
                [recipe.m_itemLootTable],
                SessionActor.ActorRef,
                playerObject,
                wizard);

            foreach (var item in rollResults.Items) {
                if (!ulong.TryParse(item.ItemId, out var itemGuid)) {
                    continue;
                }

                wizard.AddItemToInventory(itemGuid, out _);
            }
        }
    }

    private void SendCraftError(WIZARD_12_PROTOCOL.MSG_USERECIPE message)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_USERECIPE {
            RecipeName = message.RecipeName,
            FinalItemID = message.FinalItemID,
            Error = CRAFT_ERROR_GENERIC,
            Quantity = message.Quantity,
        });

}
