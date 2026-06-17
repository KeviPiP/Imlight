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
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// Handler for ResGiveSpell — hands the player a spell card during a scripted sequence
/// (e.g. the combat tutorial duel, where the lua drives the duel and the quest goals
/// feed the player their cards). This is scripted relay, not gameplay: the spell is named
/// by m_spellID (the spell-name hash); m_templateID identifies the combatant the script is
/// addressing and is NOT the spell. We add the named spell to the player's temporary combat
/// spell list and push the refreshed hand. A spell we don't have loaded is a no-op, never a
/// failure — the scripted tutorial must keep flowing.
/// </summary>
internal sealed class ResGiveSpellHandler : BaseResultHandler<ResGiveSpell> {

    private readonly ObjectSerializer _serializer = new(Versionable: false);
    private readonly PropertyFlags _combatParticipantHandFlags = (PropertyFlags) 5;
    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        // The spell is named by m_spellID (name hash), which is the spell-template cache key.
        var spell = SpellFactory.GetSpellBySpellId(Result.m_spellID);
        if (spell is null) {
            // Scripted card we don't have loaded; relay nothing rather than fail the script.
            Logger.Debug("ResGiveSpell: no loaded spell for spell ID {0}; skipping.",
                Logger.Args(Result.m_spellID));

            return true;
        }

        // The context doesn't carry a wizard reference, so query the player's session for it.
        var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        var queryTimeout = TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS);
        var queryResponse = context
            .GetPlayerRef()
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryWizardMsg, queryTimeout).Result;
        if (queryResponse?.Wizard is null) {
            Logger.Error("ResGiveSpell handler failed to retrieve character data within {0} seconds.",
                Logger.Args(QUERY_WIZARD_TIMEOUT_SECONDS));

            return true;
        }

        var wizard = queryResponse.Wizard;
        wizard.AddTemporarySpell(spell);

        // Push the updated hand to the client so the new card appears.
        var hand = new Hand {
            m_spellList = wizard.SpellbookBehavior.TemporarySpells
        };
        _serializer.Serialize(hand, _combatParticipantHandFlags, out var buffer);
        context.GetPlayerRef().Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
            ParticipantID = context.GetPlayerObj().m_globalID,
            HandData = buffer
        });

        return true;
    }

}
