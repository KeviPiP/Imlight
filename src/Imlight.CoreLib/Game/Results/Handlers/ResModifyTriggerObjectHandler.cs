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

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// Handler for ResModifyTriggerObject — toggles the visibility of a named ("trigger") object
/// in the zone for the player the result is executing for. Zone triggers (triggers.xml) use
/// this to show/hide scripted scenery and NPCs as the player progresses; e.g. the tutorial
/// area hides NPCs (Mr. Lincoln) that only belong to the post-tutorial commons.
///
/// The object is addressed by name (its zone tag) and a boolean state. We translate that into
/// the same per-player state change the dynamod path already uses: a zone-wide MSG_ENTERSTATE
/// broadcast carrying the object's tag and an "On"/"Off" state. Each entity's RenderComponent
/// matches on its zone tag and spawns/despawns itself for the sending player accordingly.
/// </summary>
internal sealed class ResModifyTriggerObjectHandler : BaseResultHandler<ResModifyTriggerObject> {

    // RenderComponent recognises exactly these two state names for show/hide.
    private const string SPAWN_STATE_NAME = "On";
    private const string DESPAWN_STATE_NAME = "Off";

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor is null) {
            return false;
        }

        string objectName = Result.m_triggerObjName;
        if (string.IsNullOrEmpty(objectName)) {
            return false;
        }

        var playerRef = context.GetPlayerRef();

        // Mirror the dynamod hide/show path: broadcast the state change to the zone's entities
        // (not the players). The entity whose zone tag matches will (de)spawn for the sender.
        var stateChangeMsg = new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
            ObjectName = objectName,
            StateName = Result.m_triggerObjState ? SPAWN_STATE_NAME : DESPAWN_STATE_NAME,
            ExclusiveToSender = true,
            Sender = playerRef
        };

        zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [stateChangeMsg],
            Sender = playerRef
        });

        return true;
    }

}
