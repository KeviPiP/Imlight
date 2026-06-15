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
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// Handler for ResModifyEntry — writes a value into the player's global or quest registry.
/// This is the write counterpart to the ReqHasEntry / ReqEntryValue requirements, and backs
/// one-time / stateful scripting (e.g. a "OnDeathFirstTime" flag set the first time a boss
/// is defeated).
/// </summary>
internal sealed class ResModifyEntryHandler : BaseResultHandler<ResModifyEntry> {

    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        // m_entryName / m_questName are ByteString; pin them to string up front.
        string entryName = Result.m_entryName;
        if (string.IsNullOrEmpty(entryName)) {
            return false;
        }

        // The context doesn't carry a wizard reference, so query the player's session for it.
        var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        var queryTimeout = TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS);
        var queryResponse = context
            .GetPlayerRef()
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryWizardMsg, queryTimeout).Result;
        var wizard = queryResponse?.Wizard;
        if (wizard is null) {
            Logger.Error("ResModifyEntry handler could not resolve the active wizard for entry {0}.",
                Logger.Args(entryName));

            return false;
        }

        // Registry values are unsigned; negative values aren't meaningful for flags/counters.
        var value = Result.m_value < 0 ? 0UL : (ulong) Result.m_value;

        if (Result.m_isQuestRegistry) {
            // Quest registry entries are scoped to a quest. Prefer the result's quest name,
            // falling back to the quest the result is executing for (if any).
            string questName = !string.IsNullOrEmpty(Result.m_questName)
                ? (string) Result.m_questName
                : GetContextQuestName(context);
            if (string.IsNullOrEmpty(questName)) {
                Logger.Warning("ResModifyEntry is a quest-registry write for entry {0} but no quest " +
                    "name was available; skipping.", Logger.Args(entryName));

                return false;
            }

            return wizard.SetQuestRegistryValue(questName, entryName, value);
        }

        return wizard.SetRegistryValue(entryName, value);
    }

    private static string GetContextQuestName(IResultContext context) => context switch {
        QuestResultContext quest => quest.QuestName,
        GenericResultContext generic => generic.QuestName,
        _ => null,
    };

}
