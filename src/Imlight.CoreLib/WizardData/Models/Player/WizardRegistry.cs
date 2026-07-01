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

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// A character's persistent registry of named flags / counters (quest completions, one-time
/// event markers such as OnDeathFirstTime, sub-quest state, etc.). Stored in its own
/// collection keyed by <see cref="CharId"/> — like quests, items and dynamods — rather than
/// embedded in the (large, frequently-read) wizard document, so flag writes are cheap and the
/// data round-trips reliably (it is a plain property model, not a readonly field).
/// </summary>
public class WizardRegistry {

    public ulong CharId { get; set; }

    /// <summary>
    /// Registry entries, keyed by entry name (quest-scoped entries are prefixed with the quest
    /// name). Value semantics are entry-specific: a flag is 1, counters hold a count.
    /// </summary>
    public Dictionary<string, ulong> Entries { get; set; } = [];

    public WizardRegistry() { }

    public WizardRegistry(ulong charId) => CharId = charId;

}
