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

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client.Documents;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// Persisted document model for a learned recipe. <see cref="Recipe"/> objects do
/// not carry a character id, so this wrapper associates a recipe with its owner.
/// </summary>
internal sealed class PersistedRecipe {

    public string Id { get; set; }
    public ulong CharId { get; set; }
    public uint RecipeNameId { get; set; }
    public ulong GlobalId { get; set; }

}

internal static class WizardRecipeCollection {

    public const string CollectionName = "WizardRecipes";
    private static readonly IDocumentStore s_store;

    static WizardRecipeCollection() {
        s_store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Adds a recipe to the recipe collection. Assumes the caller has already added
    /// this recipe to the player's behavior.
    /// </summary>
    public static void AddRecipe(ulong charId, Recipe recipe) {
        using var session = s_store.OpenSession();

        var existing = session.Query<PersistedRecipe>(collectionName: CollectionName)
            .FirstOrDefault(x => x.CharId == charId && x.RecipeNameId == recipe.m_recipeNameID);
        if (existing != null) {
            return;
        }

        var persisted = new PersistedRecipe {
            Id = $"{CollectionName}/{charId}/{recipe.m_recipeNameID}",
            CharId = charId,
            RecipeNameId = recipe.m_recipeNameID,
            GlobalId = recipe.m_globalID,
        };

        session.Store(persisted);
        var metadata = session.Advanced.GetMetadataFor(persisted);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

        session.SaveChanges();
    }

    /// <summary>
    /// Removes a recipe from the recipe collection.
    /// </summary>
    public static void RemoveRecipe(ulong charId, uint recipeNameId) {
        using var session = s_store.OpenSession();

        var existing = session.Query<PersistedRecipe>(collectionName: CollectionName)
            .FirstOrDefault(x => x.CharId == charId && x.RecipeNameId == recipeNameId);
        if (existing is null) {
            return;
        }

        session.Delete(existing);
        session.SaveChanges();
    }

    /// <summary>
    /// Tries to retrieve and rehydrate the entire recipe bag of a player.
    /// </summary>
    public static bool TryGetWizardRecipes(ulong charId, out List<Recipe> recipes) {
        using var session = s_store.OpenSession();

        var persisted = session.Query<PersistedRecipe>(collectionName: CollectionName)
            .Where(x => x.CharId == charId)
            .ToList();

        if (persisted.Count == 0) {
            recipes = null;
            return false;
        }

        recipes = [.. persisted.Select(p => new Recipe {
            m_recipeNameID = p.RecipeNameId,
            m_globalID = (GID) p.GlobalId,
        })];

        return true;
    }

    /// <summary>
    /// Removes all recipes from the recipe collection for a given character.
    /// </summary>
    public static void DeleteRecipeBag(ulong charId) {
        using var session = s_store.OpenSession();

        var recipes = session.Query<PersistedRecipe>(collectionName: CollectionName)
            .Where(x => x.CharId == charId)
            .ToList();

        foreach (var recipe in recipes) {
            session.Delete(recipe);
        }

        session.SaveChanges();
    }

}
