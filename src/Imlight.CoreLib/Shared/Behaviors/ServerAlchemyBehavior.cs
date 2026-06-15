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
 */

using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerAlchemyBehavior : IClientBehaviorProvider<ClientAlchemyBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    private static readonly int s_maxReagentStackAllowed = 999;

    public List<ulong> ReagentItemIds { get; set; }

    // Persisted ID lists for recipes and crafting slots. The hydrated object lists
    // below are [JsonIgnore] (rehydrated on database load from the dedicated
    // collections), mirroring how ReagentItemIds relates to Reagents.
    public List<uint> RecipeNameIds { get; set; }
    public List<ulong> CraftingSlotIds { get; set; }

    // Bonus crafting slots granted to the player (e.g. by crafting-rank quests). The
    // player's total concurrent timed-craft capacity is BASE + this value. A timed
    // craft occupies one slot for its cook duration; the item itself is granted
    // immediately. Persisted with the character document.
    public int BonusCraftingSlots { get; set; }

    [JsonIgnore] public List<ClientReagentItem> Reagents { get; set; }
    [JsonIgnore] public List<CraftingSlot> CraftingSlots { get; set; }
    [JsonIgnore] public List<Recipe> Recipes { get; set; }

    /// <summary>
    /// Adds a recipe to the player's recipe bag.
    /// </summary>
    /// <param name="recipe">The recipe object to be added.</param>
    /// <returns><c>true</c> if the recipe was added, <c>false</c> if it was already known.</returns>
    public bool AddRecipe(Recipe recipe) {
        Recipes ??= [];
        RecipeNameIds ??= [];

        if (Recipes.Any(x => x.m_recipeNameID == recipe.m_recipeNameID)) {
            Logger.Debug("Player already knows recipe with name id {0}.", Logger.Args(recipe.m_recipeNameID));

            return false;
        }

        Recipes.Add(recipe);
        RecipeNameIds.Add(recipe.m_recipeNameID);

        return true;
    }

    /// <summary>
    /// Removes a recipe from the player's recipe bag based on its recipe name id.
    /// </summary>
    /// <param name="recipeNameId">The recipe name id to remove.</param>
    /// <returns><c>true</c> if the recipe was removed; otherwise, <c>false</c>.</returns>
    public bool RemoveRecipe(uint recipeNameId) {
        Recipes ??= [];
        RecipeNameIds ??= [];

        var recipe = Recipes.FirstOrDefault(x => x.m_recipeNameID == recipeNameId);
        if (recipe is null) {
            Logger.Debug("Tried to remove recipe with name id {0} that does not exist in player recipe bag.",
                Logger.Args(recipeNameId));

            return false;
        }

        Recipes.Remove(recipe);
        RecipeNameIds.Remove(recipeNameId);

        return true;
    }

    /// <summary>
    /// Checks if the recipe bag contains a recipe with the specified recipe name id.
    /// </summary>
    /// <param name="recipeNameId">The recipe name id to check.</param>
    /// <returns>True if the recipe bag contains the recipe, otherwise false.</returns>
    public bool HasRecipe(uint recipeNameId) => Recipes?.Any(recipe => recipe.m_recipeNameID == recipeNameId) ?? false;

    /// <summary>
    /// Adds a crafting slot to the player's crafting slot bag.
    /// </summary>
    /// <param name="craftingSlot">The crafting slot object to be added.</param>
    /// <returns><c>true</c> if the crafting slot was added.</returns>
    public bool AddCraftingSlot(CraftingSlot craftingSlot) {
        CraftingSlots ??= [];
        CraftingSlotIds ??= [];

        CraftingSlots.Add(craftingSlot);
        CraftingSlotIds.Add(craftingSlot.m_globalID);

        return true;
    }

    /// <summary>
    /// Removes a crafting slot from the player's crafting slot bag based on its global ID.
    /// </summary>
    /// <param name="craftingSlotId">The global ID of the crafting slot to remove.</param>
    /// <returns><c>true</c> if the crafting slot was removed; otherwise, <c>false</c>.</returns>
    public bool RemoveCraftingSlot(ulong craftingSlotId) {
        CraftingSlots ??= [];
        CraftingSlotIds ??= [];

        var slot = CraftingSlots.FirstOrDefault(x => x.m_globalID == craftingSlotId);
        if (slot is null) {
            Logger.Debug("Tried to remove crafting slot with global id {0} that does not exist in player crafting bag.",
                Logger.Args(craftingSlotId));

            return false;
        }

        CraftingSlots.Remove(slot);
        CraftingSlotIds.Remove(craftingSlotId);

        return true;
    }

    /// <summary>
    /// Clears all crafting slots from the player's crafting slot bag.
    /// </summary>
    public void ClearCraftingSlots() {
        CraftingSlots?.Clear();
        CraftingSlotIds?.Clear();
    }

    /// <summary>
    /// Adds a reagent to the player's reagent bag.
    /// </summary>
    /// <param name="reagent">The reagent object to be added.</param>
    /// <returns><c>true</c> if the reagent was successfully added, <c>false</c> otherwise.</returns>
    public bool AddReagent(ClientReagentItem reagent) {
        Reagents ??= [];
        ReagentItemIds ??= [];

        // Check if the reagent is already in the reagent bag, update quantity
        var existingReagent = Reagents.FirstOrDefault(x => x.m_templateID == reagent.m_templateID);
        if (existingReagent is not null) {
            if (existingReagent.m_quantity >= s_maxReagentStackAllowed) {
                Logger.Debug("Player reagent stack is full. Cannot add reagent with global id {0}.", Logger.Args(reagent.m_globalID));

                return false;
            }

            existingReagent.m_quantity++;

            return true;
        }

        ReagentItemIds.Add(reagent.m_globalID);
        Reagents.Add(reagent);

        return true;
    }

    /// <summary>
    /// Removes a reagent from the player's reagent bag based on its unique identifier.
    /// </summary>
    /// <param name="reagentId">The unique identifier of the reagent to be removed.</param>
    /// <returns><c>true</c> if the reagent was successfully removed; otherwise, <c>false</c>.</returns>
    public bool RemoveReagent(ulong reagentId, out ClientReagentItem updatedReagent) {
        Reagents ??= [];
        ReagentItemIds ??= [];
        updatedReagent = null;

        // Get the actual item from the inventory.
        var reagent = Reagents.FirstOrDefault(x => x.m_globalID == reagentId);
        if (reagent is null) {
            Logger.Debug("Tried to remove reagent with global id {0} that does not exist in player reagent bag.",
                Logger.Args(reagentId));

            return false;
        }

        reagent.m_quantity--;
        updatedReagent = reagent;

        if (reagent.m_quantity <= 0) {
            if (!Reagents.Remove(reagent)) {
                Logger.Debug("Tried to remove reagent with global id {0} that does not exist in player inventory.",
                    Logger.Args(reagent.m_globalID));

                return false;
            }

            if (!ReagentItemIds.Remove(reagentId)) {
                Logger.Debug("Tried to remove reagent with global id {0} that does not exist in player inventory.",
                    Logger.Args(reagent.m_globalID));
                    
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if the reagent bag contains a reagent with the specified global ID.
    /// </summary>
    /// <param name="globalId">The global ID of the reagent to check.</param>
    /// <returns>True if the reagent bag contains an reagent with the specified global ID, otherwise false.</returns>
    public bool HasReagentID(ulong globalId) => Reagents?.Any(reagent => reagent.m_globalID == globalId) ?? false;

    /// <summary>
    /// Checks if the reagent bag contains a reagent with the specified template ID.
    /// </summary>
    /// <param name="templateId">The template ID of the reagent to check.</param>
    /// <returns>True if the reagent bag contains an reagent with the specified template ID, otherwise false.</returns>
    public bool HasReageant(ulong templateId) => Reagents?.Any(reagent => reagent.m_templateID == templateId) ?? false;

    /// <summary>
    /// Returns the reagent with the specified template ID.
    /// </summary>
    /// <param name="templateId">The template ID of the reagent to get.</param>
    /// <returns>Returns the reagent object with the specified template ID, otherwise null.</returns>
    public ClientReagentItem GetReagent(ulong templateId) => Reagents?.FirstOrDefault(reagent => reagent.m_templateID == templateId) ?? null;

    public ClientAlchemyBehavior GetClientBehaviorInstance() => new() {
        m_reagentBag = new ObjectBag() {
            m_maxItemStack = s_maxReagentStackAllowed,
            m_itemList = Reagents?.ConvertAll(item => (CoreObject) item) ?? []
        },
        m_craftingSlotsBag = new ObjectBag() {
            m_maxItemStack = 1,
            m_itemList = CraftingSlots?.ConvertAll(item => (CoreObject) item) ?? []
        },
        m_recipeBag = new RecipeBag() {
            m_maxItemStack = 1,
            m_itemList = Recipes?.ConvertAll(item => (CoreObject) item) ?? []
        },
        m_maxReagentStack = s_maxReagentStackAllowed
    };

}
