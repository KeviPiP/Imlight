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
 * INTERACT REAGENT
 * ========================================================================
 * 
 * PURPOSE:
 * Manages interaction mechanics for harvestable reagent entities in the game world, 
 * handling player collection and inventory addition of reagents.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Implements complex reagent quantity and rarity rolling mechanics.
 * 
 * TODO:
 * - Investigate icon sourcing for non-standard reagent types
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Reagents;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractReagentComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // Reagent harvest drop rates are configurable via the [Reagents] section of
    // Imlight.ini. Quantity is rolled as an ordered ladder: start at BaseQuantity, then
    // try to upgrade to each higher tier in turn, stopping at the first failed roll.
    // s_qtyStepChances[i] is the chance to reach quantity (i + 2).
    private static readonly int s_baseQuantity =
        System.Math.Max(1, ConfigurationManager.GetValue("Reagents.BaseQuantity", 1));
    private static readonly float s_rareChance =
        ConfigurationManager.GetValue("Reagents.RareReagentChance", 0.10f);
    private static readonly float[] s_qtyStepChances = [
        ConfigurationManager.GetValue("Reagents.ChanceForQty2", 0.10f),
        ConfigurationManager.GetValue("Reagents.ChanceForQty3", 0.05f),
        ConfigurationManager.GetValue("Reagents.ChanceForQty4", 0.03f),
        ConfigurationManager.GetValue("Reagents.ChanceForQty5", 0.015f),
        ConfigurationManager.GetValue("Reagents.ChanceForQty6", 0.005f),
    ];

    private const uint PICKUP_SOUND_TEMPLATE_ID = 1309960781;
    private const uint RARE_PICKUP_SOUND_TEMPLATE_ID = 1051090169;

    public string ServiceName => "Interact";
    public string NpcIcon {
        get {
            // todo: Not all icons come from shared worlddata.
            if (Entity.Template is not GameObjectTemplate goTemplate) {
                return string.Empty;
            }

            return $"|_Shared|WorldData|{goTemplate.m_sIcon}";
        }
    }
    public string NpcNameKey {
        get {
            if (Entity.Template is not GameObjectTemplate goTemplate) {
                return string.Empty;
            }

            // The display name is best-effort: if we cannot resolve the reagent
            // template for this node's object name we must NOT throw, otherwise the
            // interact-service memento actor crashes and the player never receives
            // the "Collect Item" option at all.
            var reagentItemTemplate = ReagentFactory.GetReagentTemplate(goTemplate.m_objectName);
            return reagentItemTemplate?.m_displayName ?? string.Empty;
        }
    }
    public string NpcTextKey => "GUI_CollectItem";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private static readonly Random s_random = new();
    private static readonly CoreObjectSerializer s_reagentAddSerializer = new(
        behaviors: SerializerFlags.None
    );
    private static readonly ObjectSerializer s_lootInfoSerializer = new(
        Versionable: false,
        Behaviors: SerializerFlags.None
    );

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_adjectiveList is not null
        && goTemplate.m_adjectiveList.Any(x => string.Equals(x, "Reagent", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [ new InteractableOption { m_serviceName = ServiceName }];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var nodeName = (Entity.Template as GameObjectTemplate)?.m_objectName ?? "<?>";
        Logger.Debug("Reagent interaction: node '{0}', char {1}.", Logger.Args(nodeName, playerCharacter.CharId));

        var quantity = RollReagentQuantity();
        var reagent = GetReagent(playerCharacter.CharId, quantity);
        if (reagent is null) {
            var objectName = (Entity.Template as GameObjectTemplate)?.m_objectName ?? "<unknown>";
            Logger.Error("Failed to resolve a reagent for node '{0}' (character {1}, quantity {2}). " +
                "No reagent template matched the harvest heuristic for this object name.",
                Logger.Args(objectName, playerCharacter.CharId, quantity));

            return;
        }

        // Determine if the reagent is rare and get the rare reagent if it is.
        var isRare = IsRareReagent();
        var rareReagent = isRare ? GetRareReagent(playerCharacter.CharId) : null;

        // Add all reagents to the player's inventory.
        for (int i = 0; i < quantity; i++) {
            playerCharacter.AddReagent(reagent);
        }
        if (isRare) {
            playerCharacter.AddReagent(rareReagent);
        }

        // Inform the game client that the player has gathered reagents.
        var reagents = isRare ? new[] { reagent, rareReagent } : [reagent];
        SendPlayerReagentAddMessage(playerActor, reagents, playerCharacter.CharId);

        // Inform the game client that they have gathered loot.
        SendPlayerLootInfoMessage(playerActor, new Dictionary<ulong, int> {
            [reagent.m_templateID] = reagent.m_quantity,
            [rareReagent?.m_templateID ?? 0] = rareReagent?.m_quantity ?? 0,
        }, playerCharacter.CharId);

        // Play the pickup sound.
        if (isRare) {
            SendPlayerRarePickupSound(playerActor);
        }
        else {
            SendPlayerPickupSound(playerActor);
        }

        var leaveServiceRangeMsg = new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE {
            MobileID = Entity.ActiveGameObject.m_globalID
        };
        playerActor.Tell(leaveServiceRangeMsg);

        // Finally, destroy this entity.
        Entity.DeleteObject();
    }

    private ClientReagentItem GetReagent(ulong charId, int quantity) {
        var goTemplate = Entity.Template as GameObjectTemplate;
        var item = ReagentFactory.GetHarvestable(goTemplate.m_objectName);

        if (item is null) {
            return null;
        }

        item.m_quantity = quantity;
        item.m_characterId = (GID) charId;

        return item;
    }

    private ClientReagentItem GetRareReagent(ulong charId) {
        var goTemplate = Entity.Template as GameObjectTemplate;
        var item = ReagentFactory.GetHarvestableRareVariant(goTemplate.m_objectName);

        if (item is null) {
            return null;
        }

        item.m_quantity = 1;
        item.m_characterId = (GID) charId;

        return item;
    }

    private static LootInfoList GetLootInfoList(Dictionary<ulong, int> items) => new() {
        m_loot = [.. items.Select(item => (LootInfo) new ItemLootInfo {
            m_itemID = (GID) item.Key,
            m_lootType = LOOT_TYPE.LOOT_TYPE_ITEM,
            m_numItems = item.Value
        })]
    };

    private static void SendPlayerReagentAddMessage(IActorRef playerActor, ClientReagentItem[] reagents, ulong globalId) {
        foreach (var reagent in reagents) {
            // Serialize the reagent and send it to the player.
            if (!s_reagentAddSerializer.Serialize(reagent, 1, out var reagentData)) {
                Logger.Error("Failed to serialize reagent {0}", 
                    Logger.Args(reagent));
                    
                return;
            }

            var newReagentMsg = new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
                GlobalID = globalId,
                Data = reagentData,
            };

            playerActor.Tell(newReagentMsg);
        }
    }

    private static void SendPlayerLootInfoMessage(IActorRef playerActor, Dictionary<ulong, int> reagents, ulong globalId) {
        // Remove any reagents with a quantity of 0.
        reagents = reagents.Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value);

        var lootInfoList = GetLootInfoList(reagents);

        // Serialize the loot info list and send it to the player.
        if (!s_lootInfoSerializer.Serialize(lootInfoList, 1, out var lootInfoData)) {
            Logger.Error("Failed to serialize loot info list {0}", 
                Logger.Args(lootInfoList));
                
            return;
        }

        var lootInfoMsg = new WIZARD_12_PROTOCOL.MSG_LOOT {
            GlobalID = globalId,
            LootList = lootInfoData,
        };

        playerActor.Tell(lootInfoMsg);
    }

    private static void SendPlayerPickupSound(IActorRef playerActor) {
        var soundId = new GID();
        soundId.MParts.TemplateId = PICKUP_SOUND_TEMPLATE_ID;

        var soundMsg = new GAME_5_PROTOCOL.MSG_PLAYSOUND {
            SoundID = soundId,
        };

        playerActor.Tell(soundMsg);
    }

    private static void SendPlayerRarePickupSound(IActorRef playerActor) {
        var soundId = new GID();
        soundId.MParts.TemplateId = RARE_PICKUP_SOUND_TEMPLATE_ID;

        var soundMsg = new GAME_5_PROTOCOL.MSG_PLAYSOUND {
            SoundID = soundId,
        };

        playerActor.Tell(soundMsg);
    }

    private static bool IsRareReagent()
        => s_random.NextDouble() < s_rareChance;

    private static int RollReagentQuantity() {
        var quantity = s_baseQuantity;

        // Try to upgrade to each higher quantity tier in order, stopping at the first
        // failed roll. s_qtyStepChances[tier - 2] is the chance to reach that tier.
        var maxTier = s_qtyStepChances.Length + 1;
        for (var tier = quantity + 1; tier <= maxTier; tier++) {
            if (s_random.NextDouble() < s_qtyStepChances[tier - 2]) {
                quantity = tier;
            }
            else {
                break;
            }
        }

        return quantity;
    }

}