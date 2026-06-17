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
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// Handler for ResDrawHand — instructs the client to (re)draw the player's combat hand.
/// Paired with ResGiveSpell in scripted duels (the tutorial combat); after the scripted
/// cards have been handed out, this pushes the current hand so the client renders it.
/// </summary>
internal sealed class ResDrawHandHandler : BaseResultHandler<ResDrawHand> {

    private readonly ObjectSerializer _serializer = new(Versionable: false);
    private readonly PropertyFlags _combatParticipantHandFlags = (PropertyFlags) 5;
    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        // The context doesn't carry a wizard reference, so query the player's session for it.
        var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        var queryTimeout = TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS);
        var queryResponse = context
            .GetPlayerRef()
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryWizardMsg, queryTimeout).Result;
        if (queryResponse?.Wizard is null) {
            Logger.Error("ResDrawHand handler failed to retrieve character data within {0} seconds.",
                Logger.Args(QUERY_WIZARD_TIMEOUT_SECONDS));

            // Scripted relay: never stall the tutorial flow on a transient query miss.
            return true;
        }

        var wizard = queryResponse.Wizard;
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
