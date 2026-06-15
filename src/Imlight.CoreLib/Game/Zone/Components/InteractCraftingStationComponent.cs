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
 * INTERACT CRAFTING STATION
 * ========================================================================
 *
 * PURPOSE:
 * Manages interaction logic for crafting station (alchemy) NPCs/objects in the
 * game world. Attaches to any entity whose template carries an
 * AlchemyBehaviorTemplate and opens the crafting station UI for the player.
 *
 * NOTE:
 * Auto-discovered by ZoneEntityComponentRegistry via reflection (no manual
 * registration required).
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractCraftingStationComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "CraftingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_Crafting";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go
        && go.m_behaviors?.Any(b => b is AlchemyBehaviorTemplate) == true;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new AlchemyStationOption {
                m_serviceName = ServiceName,
                m_displayKey = DisplayKey,
                m_iconKey = NpcIcon,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // Open the alchemy/crafting station UI on the client. We don't currently
        // restrict the recipe list per-station, so AllowedRecipes is left empty,
        // allowing all learned/equipped recipes.
        var alchemyStationMsg = new WIZARD_12_PROTOCOL.MSG_ALCHEMYSTATION {
            AllowedRecipes = new ByteString(System.Array.Empty<byte>()),
        };

        playerActor.Tell(alchemyStationMsg);
    }

}
