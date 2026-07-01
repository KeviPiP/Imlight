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
 * RECIPE FACTORY MANAGEMENT SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * Provides centralized loading, management, and retrieval of recipe templates.
 * Recipes physically live in the separate Recipes-WorldData.wad archive (not
 * referenced by Root.wad's TemplateManifest), so this factory loads them via
 * RecipeArchiveLoader instead of inheriting RootDirectoryResourceSingleton
 * (whose Load() is hardwired to the Root.wad loader).
 *
 * USAGE EXAMPLE:
 * RecipeFactory.Initialize();
 * var recipe = RecipeFactory.GetRecipeByName("Recipe Name");
 *
 * NOTE:
 * The index is built eagerly once (~12,402 files), mirroring ReagentFactory.
 *
 * Created by: Imlight
 * Version: KALI 1.0
 */

using System;
using System.Collections.Generic;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Recipes;

/// <summary>
/// Manages the loading, caching, and retrieval of recipe templates from the
/// Recipes-WorldData.wad archive. Indexes recipes by name (lowercased) and by
/// output item GID.
/// </summary>
public static class RecipeFactory {

    private const string RECIPE_DIRECTORY = "ObjectData/";

    private static readonly Dictionary<string, RecipeTemplate> s_recipesByName = [];
    private static readonly Dictionary<ulong, RecipeTemplate> s_recipesByItemId = [];
    private static bool s_loaded;

    /// <summary>
    /// Eagerly loads and indexes all recipe templates. Safe to call multiple times;
    /// the load only occurs once. Guards against the recipe WAD being unavailable so
    /// that it never crashes server boot.
    /// </summary>
    public static void Initialize() {
        if (s_loaded) {
            return;
        }
        s_loaded = true;

        try {
            LoadRecipes();
        }
        catch (Exception ex) {
            Logger.Error("Failed to load recipe templates from {0}: {1}",
                Logger.Args(RecipeArchiveLoader.WAD_NAME, ex.Message));
        }
    }

    /// <summary>
    /// Retrieves a recipe template by its recipe name (case-insensitive).
    /// </summary>
    /// <param name="recipeName">The recipe name.</param>
    /// <returns>The matching <see cref="RecipeTemplate"/>, or null if none was found.</returns>
    public static RecipeTemplate GetRecipeByName(string recipeName) {
        Initialize();

        if (string.IsNullOrEmpty(recipeName)) {
            return null;
        }

        s_recipesByName.TryGetValue(recipeName.ToLower(), out var template);
        return template;
    }

    /// <summary>
    /// Retrieves a recipe template by the GID of its output item.
    /// </summary>
    /// <param name="itemId">The output item's GID value.</param>
    /// <returns>The matching <see cref="RecipeTemplate"/>, or null if none was found.</returns>
    public static RecipeTemplate GetRecipeByItemId(ulong itemId) {
        Initialize();

        s_recipesByItemId.TryGetValue(itemId, out var template);
        return template;
    }

    private static void LoadRecipes() {
        var files = RecipeArchiveLoader.GetDirectoryStream(RECIPE_DIRECTORY);
        if (files.Count == 0) {
            Logger.Error("No recipe templates were found in {0}. The crafting system will be unavailable.",
                Logger.Args(RecipeArchiveLoader.WAD_NAME));

            return;
        }

        var serializer = new BindSerializer();
        var count = 0;

        foreach (var (fileRecord, fileStream) in files) {
            if (fileStream is null) {
                continue;
            }

            if (!serializer.Deserialize<RecipeTemplate>(fileStream?.ToArray(), out var template)) {
                Logger.Error("Could not deserialize {0} as {1}",
                    Logger.Args(fileRecord.FileName, nameof(RecipeTemplate)));

                continue;
            }

            if (!string.IsNullOrEmpty(template.m_recipeName)) {
                s_recipesByName[template.m_recipeName.ToLower()] = template;
            }

            if (template.m_itemID != 0) {
                s_recipesByItemId[(ulong) template.m_itemID] = template;
            }

            count++;
        }

        Logger.Information("Loaded {0} recipe templates.", Logger.Args(count));
    }

}
