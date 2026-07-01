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
 * LOOT GRANTER
 * ========================================================================
 *
 * PURPOSE:
 * Applies a rolled DropTableResult to a wizard: awards gold, experience,
 * training points, items and potion slots, then sends the loot popup to the
 * game client. Shared by every system that grants drop-table loot (quest /
 * trigger results via ResDropTableHandler, and combat victory via
 * CombatDuelComponent) so the grant logic lives in exactly one place.
 */

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.DropTables;

public static class LootGranter {

    private const uint LOOT_LIST_SERIALIZATION_FLAGS = 4;

    /// <summary>
    /// Applies a rolled drop-table result to the wizard and informs their game client.
    /// </summary>
    public static void Grant(IActorRef playerActor, Wizard wizard, DropTableResult rollResults) {
        if (rollResults is null || wizard is null || playerActor is null) {
            return;
        }

        UpdateWizardGold(playerActor, wizard, rollResults.GoldAmount);
        UpdateWizardXP(playerActor, rollResults.ExperienceAmount);
        UpdateWizardTP(playerActor, wizard, rollResults.TrainingPoints);
        UpdateCharacterItems(wizard, rollResults.Items);
        GiveTreasureCards(playerActor, wizard, rollResults.TreasureCards);
        AddNewSpells(playerActor, wizard, rollResults.SpellCards);
        SendLootInfoToClient(playerActor, rollResults, wizard);

        if (rollResults.GrantsPotionSlot) {
            UpdateWizardPotionMax(playerActor, wizard);
        }
    }

    private static void AddNewSpells(IActorRef playerActor, Wizard wizard, List<DropSpellCardResult> spellCards) {
        if (spellCards is null || spellCards.Count == 0) {
            return;
        }

        // Add each spell to the wizard's spellbook.
        foreach (var spellCard in spellCards) {
            if (!ulong.TryParse(spellCard.SpellTemplateID, out var spellCardID)) {
                continue;
            }

            var spell = SpellFactory.GetSpell((uint) spellCardID);

            var spellLearnedSuccess = wizard.LearnSpell(spell);
            if (!spellLearnedSuccess) {
                Logger.Error("Wizard {0} attempted to train spell {1} but failed to learn it.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), spellCardID));

                continue;
            }

            // send message to client saying to add the spell to their book
            var addSpellMsg = new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK() {
                SpellID = (int) spellCardID
            };
            playerActor.Tell(addSpellMsg);

            // send message to client saying we completed training woo!
            var trainCompleteMsg = new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE() {
                SpellID = spellCardID,
                DisplayText = "GUI_00000902",
                Success = 1
            };
            playerActor.Tell(trainCompleteMsg);
        }
    }

    private static void GiveTreasureCards(IActorRef playerActor, Wizard wizard, List<DropTreasureCardResult> treasureCards) {
        if (treasureCards is null || treasureCards.Count == 0) {
            return;
        }

        // Add each treasure card to the wizard's inventory.
        foreach (var treasureCard in treasureCards) {
            if (!ulong.TryParse(treasureCard.SpellID, out var treasureCardID)) {
                continue;
            }
            var addSpellMsg = new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = (int) treasureCardID,
                EnchantmentID = 0,
            };

            playerActor.Tell(addSpellMsg);

            // Persist the treasure card in the wizard's database record.
            wizard.SpellbookBehavior.AddTreasureCard((uint) treasureCardID);
            WizardCollection.AddTreasureCard(wizard, (uint) treasureCardID);
        }
    }

    private static void UpdateWizardGold(IActorRef playerActor, Wizard wizard, int goldDelta) {
        if (goldDelta == 0) {
            return;
        }

        // Add gold to the wizard. This will save their data, but not inform their game client.
        wizard.AddGold(goldDelta);

        // Now, inform the game client their gold has been updated.
        // This only changes the character page. It does not show a popup or anything.
        // The popup comes from the network LootInfoList.
        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        playerActor.Tell(networkMessage);
    }

    private static void UpdateWizardXP(IActorRef playerActor, int xpDelta) {
        if (xpDelta == 0) {
            return;
        }

        // XP is simple and we only need to inform the SessionActor.
        var internalMsg = new CHARACTER_103_PROTOCOL.MSG_GAINXP {
            XP = xpDelta
        };
        playerActor.Tell(internalMsg);

        // That's it. The SessionActor will inform the game client, and level them up if needed.
    }

    private static void UpdateWizardTP(IActorRef playerActor, Wizard wizard, int tpDelta) {
        if (tpDelta == 0) {
            return;
        }

        // Set the new amount of training points for the wizard.
        var oldTP = wizard.MagicSchoolBehavior.TrainingPoints;
        var newTP = Math.Max(0, oldTP + tpDelta);
        if (newTP == oldTP) {
            return;
        }

        wizard.UpdateTrainingPoints(newTP);

        // Inform the game client of the new TP amount.
        var msg = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = (ushort) newTP
        };
        playerActor.Tell(msg);
    }

    private static void UpdateCharacterItems(Wizard wizard, List<DropItemResult> items) {
        if (items is null || items.Count == 0) {
            return;
        }

        // Add each item to the wizard's inventory.
        foreach (var item in items) {
            if (!ulong.TryParse(item.ItemId, out var itemGuid)) {
                continue;
            }

            if (!wizard.AddItemToInventory(itemGuid, out var _)) {
                Logger.Error("LootGranter failed to add item {0} to wizard {1}'s inventory.",
                    Logger.Args(item.ItemId, wizard.CharId));

                continue;
            }
        }
    }

    private static void UpdateWizardPotionMax(IActorRef playerActor, Wizard wizard) {
        var currentWizardMaxPots = wizard.GameStats.m_potionMax;
        var newWizardMaxPots = currentWizardMaxPots + 1;

        wizard.UpdatePotions(newWizardMaxPots, newWizardMaxPots);

        // Inform the player's game client that their potion charge has been updated.
        var potionChargeUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS {
            PotionMax = newWizardMaxPots,
            PotionCharge = newWizardMaxPots
        };
        playerActor.Tell(potionChargeUpdateMsg);
    }

    private static void SendLootInfoToClient(IActorRef playerActor, DropTableResult results, Wizard wizard) {
        // Inform the game client of the loot results.
        if (results.Items.Count == 0
            && results.TreasureCards.Count == 0
            && results.SpellCards.Count == 0) {
            return;
        }

        // Convert the loot results into something we can send over the network.
        var networkLootList = DropTableConverter.ToLootInfoList(results);
        var serializer = new ObjectSerializer(Versionable: false);
        if (!serializer.Serialize(networkLootList, LOOT_LIST_SERIALIZATION_FLAGS, out var serializedLootList)) {
            Logger.Error("LootGranter failed to serialize loot list for network transmission.");

            return;
        }

        var lootMsg = new WIZARD_12_PROTOCOL.MSG_LOOT() {
            GlobalID = wizard.CharId,
            LootList = serializedLootList
        };

        playerActor.Tell(lootMsg);
    }

}
