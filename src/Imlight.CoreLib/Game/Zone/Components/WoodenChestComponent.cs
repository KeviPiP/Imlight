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
 * WOODEN CHEST COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * This component handles the interaction logic for wooden chests in the game.
 * The gold value is randomly generated between 10 and 100.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Phill
 * Version: KALI 1.0
 * Last Updated: 10/09/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class WoodenChestComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    private const int ANIMATION_TIME = 1;

    // Internal mapping of chest template id -> chest category name. This is game data
    // (which world objects are which kind of chest) and lives in code; the [Chests]
    // config section only assigns a gold range to each human-readable category name.
    private static readonly Dictionary<uint, string> s_chestNames = BuildChestNames();

    // Gold range per chest category name, loaded from [Chests] (e.g. "Silver Chest = 50-250").
    private static readonly Dictionary<string, (int Min, int Max)> s_goldByName = LoadGoldByName();

    private static readonly uint s_openSoundTemplateID = 1374674914;
    private readonly TimeSpan _chestOpenAnimationTimeSpan = TimeSpan.FromSeconds(ANIMATION_TIME);

    public string ServiceName => "Interact";
    public string NpcIcon => "GUI/QuestButtons/Art_Quest_Chest_Wood.dds";
    // Derive the name from the chest's own template so each tier (Wooden, Silver, ...)
    // displays correctly, instead of labelling every chest "Wooden Chest".
    public string NpcNameKey => (Entity.Template as GameObjectTemplate)?.m_displayName
        ?? "WizardGameObjects_00000054";
    public string NpcTextKey => "GUI_ChestInteract";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => "Open";
    public string InteractWizBang => "";
    public string DisplayKey => "";

    public ITimerScheduler Timers { get; set; }

    private bool _hasInteracted = false;

    public static bool ShouldAttachToEntity(CoreTemplate template) =>
        template is GameObjectTemplate goTemplate
        && s_chestNames.ContainsKey(goTemplate.m_templateID);

    // Parses "[Chests]" entries of the form  <Chest Name> = <minGold>-<maxGold>.
    private static Dictionary<string, (int Min, int Max)> LoadGoldByName() {
        var result = new Dictionary<string, (int Min, int Max)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in ConfigurationManager.GetSection("Chests")) {
            var range = value.Split('-');
            if (range.Length == 2
                && int.TryParse(range[0].Trim(), out var min)
                && int.TryParse(range[1].Trim(), out var max)
                && max >= min) {
                result[name.Trim()] = (min, max);
            }
        }

        return result;
    }

    // Game-data mapping of chest template ids to their category name. Grouped by category;
    // add ids here as new chest types are identified. The gold range for each name is set
    // in the [Chests] config section.
    private static Dictionary<uint, string> BuildChestNames() {
        var map = new Dictionary<uint, string>();

        void Add(string name, params uint[] ids) {
            foreach (var id in ids) {
                map[id] = name;
            }
        }

        Add("Wooden Chest", 77823, 77825, 77826, 77827, 147082, 224081, 310073, 358262, 465271,
            470099, 617142, 643235, 1217452, 1217999, 1233729, 1370122, 1413148, 1488319, 1520553,
            1524971, 1562823);
        Add("Silver Chest", 77824, 77828, 77829, 77830, 147084, 224082, 310074, 358264, 465272,
            470100, 617143, 643236, 1206816, 1370123, 1378000, 1413149, 1488325, 1520554, 1524972,
            1562824);
        Add("Golden Chest", 4399);
        Add("Raid Chest", 1562728, 1562729, 1562730, 1562731, 1562732, 1562740, 1562741, 1562742,
            1634591, 1702049, 1702050, 1702051, 1702052, 1749230, 1749231, 1749232, 1749233, 1749234,
            1749235);
        Add("Boss Chest", 726193, 726194, 726195, 1300185, 1378001, 1378002, 1378011, 1378012,
            1401295, 1413000, 1413002, 1413003, 1413004, 1448170);

        return map;
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new ServiceOptionBase() {
                m_displayKey = DisplayKey,
                m_forceInteract = false,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
                m_serviceIndex = 0
            }
         ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (_hasInteracted) {
            return;
        }

        _hasInteracted = true;

        // Resolve this chest's category, then roll gold from its configured range.
        var templateId = (Entity.Template as GameObjectTemplate)?.m_templateID ?? 0;
        var chestName = s_chestNames.GetValueOrDefault(templateId, "Wooden Chest");
        var (minGold, maxGold) = s_goldByName.TryGetValue(chestName, out var range) ? range : (10, 100);
        var goldAmount = Random.Shared.Next(minGold, maxGold + 1);

        // DEBUG: bracket the interaction so we can tell where it dies. If "gold before/after"
        // logs but the client shows nothing, the server logic worked and the issue is the
        // (non-versionable) MSG_LOOT serialization the client can't parse.
        Logger.Debug("Chest interaction start: char {0}, gold before {1}, granting {2}.",
            Logger.Args(playerCharacter.CharId, playerCharacter.GameStats.m_currentGold, goldAmount));

        SendLoot(playerActor, goldAmount);
        UpdateGold(playerActor, playerCharacter, goldAmount);
        PlaySound(playerActor);
        TriggerChestAnimation();

        Logger.Debug("Chest interaction end: char {0}, gold after {1}.",
            Logger.Args(playerCharacter.CharId, playerCharacter.GameStats.m_currentGold));

        var delayedDeleteMsg = new ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT();
        Timers.StartSingleTimer("deletechestobject", delayedDeleteMsg, _chestOpenAnimationTimeSpan);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT))]
    private void ReceivedDelayedDeleteMessage(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT _) 
        => Entity.DeleteObject();

    private void TriggerChestAnimation() {
        var chestStates = new uint[] { StringHash.Compute(StateName), StringHash.Compute("NotInteracting") };
        for (int i = 0; i < chestStates.Length; i++) {
            var enterState = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                Data = "",
                GameObjectID = Entity.ActiveGameObject.m_globalID,
                IgnoreIfCurrentStateIsOff = 0,
                State = chestStates[i]
            };

            var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = enterState,
                Selfless = false,
            };

            Entity.ZoneRef.Tell(broadcastMsg);
        }
    }

    private void SendLoot(IActorRef playerActor, int goldAmount) {
        var lootInfoList = new LootInfoList {
            m_loot = [],
            m_goldInfo = new GoldLootInfo {
                m_goldAmount = goldAmount,
                m_lootType = LOOT_TYPE.LOOT_TYPE_GOLD,
            },
            m_lootRarityList = new LootRarityList {
                m_loot = []
            }
        };

        var serializer = new ObjectSerializer(
            false,
            Behaviors: SerializerFlags.None
        );

        if (!serializer.Serialize(lootInfoList, 4, out var data)) {
            Logger.Error("Failed to serialize LootInfoList.");

            return;
        }

        var loot = new WIZARD_12_PROTOCOL.MSG_LOOT {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            LootList = data
        };

        playerActor.Tell(loot);
    }

    private static void UpdateGold(IActorRef playerActor, Wizard playerCharacter, int goldAmount) {
        // Add first, then report the running TOTAL — the client treats MSG_UPDATEGOLD.Gold as
        // the wizard's new total balance (confirmed in the packet dump), not the amount gained.
        // Sending the delta here made the character UI show only the last gold picked up.
        playerCharacter.AddGold(goldAmount);

        var updateGold = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = playerCharacter.GameStats.m_currentGold,
            MaxGold = playerCharacter.GameStats.m_baseGoldPouch
        };

        playerActor.Tell(updateGold);
    }

    private static void PlaySound(IActorRef playerActor) {
        var soundId = new GID(s_openSoundTemplateID);
        var playSound = new GAME_5_PROTOCOL.MSG_PLAYSOUND {
            SoundID = soundId,
            ReinteractTime = 0,
            SoundFilename = "",
            StartDelay = 0,
            PlayAtMusicVolume = 0,
        };

        playerActor.Tell(playSound);
    }

}
