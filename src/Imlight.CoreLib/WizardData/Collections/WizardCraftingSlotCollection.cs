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
/// Persisted document model for an active crafting slot. <see cref="CraftingSlot"/>
/// objects do not carry a character id, so this wrapper associates a slot with its owner.
/// </summary>
internal sealed class PersistedCraftingSlot {

    public string Id { get; set; }
    public ulong CharId { get; set; }
    public ulong GlobalId { get; set; }
    public string RecipeName { get; set; }
    public int TimeFinished { get; set; }

}

internal static class WizardCraftingSlotCollection {

    public const string CollectionName = "WizardCraftingSlots";
    private static readonly IDocumentStore s_store;

    static WizardCraftingSlotCollection() {
        s_store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Adds a crafting slot to the crafting slot collection. Assumes the caller has
    /// already added this slot to the player's behavior.
    /// </summary>
    public static void AddCraftingSlot(ulong charId, CraftingSlot slot) {
        using var session = s_store.OpenSession();

        var existing = session.Query<PersistedCraftingSlot>(collectionName: CollectionName)
            .FirstOrDefault(x => x.CharId == charId && x.GlobalId == slot.m_globalID);
        if (existing != null) {
            return;
        }

        var persisted = new PersistedCraftingSlot {
            Id = $"{CollectionName}/{charId}/{(ulong) slot.m_globalID}",
            CharId = charId,
            GlobalId = slot.m_globalID,
            RecipeName = slot.m_recipeName,
            TimeFinished = slot.m_timeFinished,
        };

        session.Store(persisted);
        var metadata = session.Advanced.GetMetadataFor(persisted);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

        session.SaveChanges();
    }

    /// <summary>
    /// Removes a crafting slot from the crafting slot collection.
    /// </summary>
    public static void RemoveCraftingSlot(ulong charId, ulong globalId) {
        using var session = s_store.OpenSession();

        var existing = session.Query<PersistedCraftingSlot>(collectionName: CollectionName)
            .FirstOrDefault(x => x.CharId == charId && x.GlobalId == globalId);
        if (existing is null) {
            return;
        }

        session.Delete(existing);
        session.SaveChanges();
    }

    /// <summary>
    /// Tries to retrieve and rehydrate the entire crafting slot bag of a player.
    /// </summary>
    public static bool TryGetWizardCraftingSlots(ulong charId, out List<CraftingSlot> craftingSlots) {
        using var session = s_store.OpenSession();

        var persisted = session.Query<PersistedCraftingSlot>(collectionName: CollectionName)
            .Where(x => x.CharId == charId)
            .ToList();

        if (persisted.Count == 0) {
            craftingSlots = null;
            return false;
        }

        craftingSlots = [.. persisted.Select(p => new CraftingSlot {
            m_globalID = (GID) p.GlobalId,
            m_recipeName = p.RecipeName,
            m_timeFinished = p.TimeFinished,
        })];

        return true;
    }

    /// <summary>
    /// Removes all crafting slots from the crafting slot collection for a given character.
    /// </summary>
    public static void DeleteCraftingSlotBag(ulong charId) {
        using var session = s_store.OpenSession();

        var slots = session.Query<PersistedCraftingSlot>(collectionName: CollectionName)
            .Where(x => x.CharId == charId)
            .ToList();

        foreach (var slot in slots) {
            session.Delete(slot);
        }

        session.SaveChanges();
    }

}
