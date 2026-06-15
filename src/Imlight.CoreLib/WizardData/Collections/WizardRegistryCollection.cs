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
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// Persists each character's <see cref="WizardRegistry"/> in its own collection, keyed by
/// character id — the same pattern used for items, dynamods and quest instances.
/// </summary>
public static class WizardRegistryCollection {

    public const string CollectionName = "WizardRegistry";

    private static readonly IDocumentStore s_store;

    static WizardRegistryCollection() {
        s_store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Retrieves the registry document for a character, or <c>null</c> if none exists yet.
    /// </summary>
    public static WizardRegistry GetRegistry(ulong charId) {
        using var session = s_store.OpenSession();

        return session.Query<WizardRegistry>(collectionName: CollectionName)
            .FirstOrDefault(r => r.CharId == charId);
    }

    /// <summary>
    /// Upserts a character's registry entries. Creates the document on first save, otherwise
    /// overwrites the existing entries.
    /// </summary>
    public static bool SaveRegistry(ulong charId, Dictionary<string, ulong> entries) {
        using var session = s_store.OpenSession();

        var existing = session.Query<WizardRegistry>(collectionName: CollectionName)
            .FirstOrDefault(r => r.CharId == charId);

        if (existing is null) {
            var registry = new WizardRegistry(charId) {
                Entries = new Dictionary<string, ulong>(entries),
            };
            session.Store(registry);

            var metaData = session.Advanced.GetMetadataFor(registry);
            metaData[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        }
        else {
            existing.Entries = new Dictionary<string, ulong>(entries);
        }

        session.SaveChanges();

        return true;
    }

}
