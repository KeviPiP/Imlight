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
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed class TutorialService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string TUTORIAL_QUEST_NAME = "WC-TUT-C05-001";
    private const uint TUTORIAL_NAME_STRING_ID = 600062081;
    private const string TUTORIAL_EXTERIOR_ZONE_NAME_CONTENTS = "Tutorial_Exterior";
    private const string TUTORIAL_INTERIOR_ZONE_NAME_CONTENTS = "Tutorial_Interior";

    // Starter gear. Character creation (Wizard._defaultItems / InitializeDefaultInventory)
    // already places these in the new wizard's inventory but leaves them UNEQUIPPED. The live
    // first-login flow (see packet dump) equips them before anything else — most importantly the
    // deck, since spells can only be stocked into an equipped deck. We equip them here on first
    // login so a tutorial-skipped wizard is fully outfitted and ready to play.
    private const ulong STARTER_DECK  = 126983;   // Starter Deck
    private const ulong STARTER_WAND  = 1475826;  // Wand of the Seven Schools
    private const ulong STARTER_HAT   = 1317124;  // QA hat
    private const ulong STARTER_ROBE  = 1317129;  // QA robe
    private const ulong STARTER_BOOTS = 1317234;  // QA boots
    private static readonly ulong[] s_starterEquipment = {
        STARTER_DECK, STARTER_WAND, STARTER_HAT, STARTER_ROBE, STARTER_BOOTS,
    };

    // XP the live tutorial awards a fresh wizard (packet dump: MSG_UPDATEXP XP=33, level 1).
    private const int TUTORIAL_XP_REWARD = 33;

    private static readonly Dictionary<MagicSchool, string> s_starterSpell = new() {
        { MagicSchool.Fire, "Fire Cat" }, { MagicSchool.Ice, "Frost Beetle" },
        { MagicSchool.Storm, "Thunder Snake" }, { MagicSchool.Myth, "Bloodbat" },
        { MagicSchool.Death, "Dark Sprite" }, { MagicSchool.Balance, "Scarab" },
        { MagicSchool.Life, "Imp" },
    };

    private readonly ObjectSerializer _serializer = new(
        Versionable: false,
        Behaviors: SerializerFlags.None
    );
    private readonly TutorialInfo _tutorialInfo = new() {
        m_tutorialNameID = TUTORIAL_NAME_STRING_ID,
        m_tutorialStage = 0,
    };

    internal static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TutorialService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ATTACH))]
    private void ReceivePostAttach(GAME_5_PROTOCOL.MSG_ATTACH msg) {
        // Tutorial UI: only when the player is actually in a tutorial zone. The client's lua
        // drives the tutorial; we just hand it the tutorial info. (If the tutorial is skipped,
        // new wizards never enter these zones and this block is simply not run.)
        var zoneName = msg.ZoneName.ToString();
        if (   zoneName.Contains(TUTORIAL_EXTERIOR_ZONE_NAME_CONTENTS)
            || zoneName.Contains(TUTORIAL_INTERIOR_ZONE_NAME_CONTENTS)) {
            if (_serializer.Serialize(_tutorialInfo,
                                      PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit,
                                      out var tutorialInfoBuffer)) {
                var tutorialMsg = new GAME_5_PROTOCOL.MSG_TUTORIALS() {
                    GlobalID = 1,
                    Remove = 0,
                    TutorialInfo = tutorialInfoBuffer
                };
                SendToSocket(tutorialMsg);
                SendToSocket(tutorialMsg);
            }
            else {
                Logger.Error("Failed to serialize tutorial info");
            }
        }

        // NOTE: the starter-kit grant is NOT done here. MSG_ATTACH arrives before the player's
        // Wizard reference is loaded, so GetActiveWizard() is null at this point. The grant runs
        // from MSG_ATTACHCOMPLETE below, which is fired once the wizard is fully attached.
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        // First-login onboarding grant: equip the starter gear + first spell + XP. Idempotent,
        // and fires whether or not the tutorial runs — so a tutorial-skipped wizard ends up in
        // the same fully-outfitted state the live tutorial leaves them in. Runs here (not on
        // MSG_ATTACH) because the Wizard reference is only loaded by attach-complete time.
        GrantStarterKit();
    }

    /// <summary>
    /// Brings a new wizard to the same outfitted state the live tutorial leaves them in,
    /// reconstructed 1:1 from the first-login packet dump:
    ///   1. Equip the starter gear char creation left in the inventory (deck, wand, clothing).
    ///      The deck MUST be equipped first — it's the container spells are stocked into.
    ///   2. Learn the school's first spell and add three copies to the equipped deck
    ///      (MSG_ADDSPELLTOBOOK + 3x MSG_ADDSPELLTODECK, Success=1).
    ///   3. Award the tutorial XP.
    /// Idempotent — once a deck is equipped, the kit has already been granted.
    /// </summary>
    private void GrantStarterKit() {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            Logger.Warning("Starter kit: active wizard not available at attach-complete; skipping grant.");

            return;
        }

        // Idempotency marker: an equipped deck means the kit was already granted.
        if (wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Deck) is not null) {
            return;
        }

        Logger.Information("Granting starter kit to {0} (school {1})...",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), wizard.MagicSchoolBehavior.MagicSchool));

        // 1. Equip the default starter gear (already in inventory from char creation).
        foreach (var templateId in s_starterEquipment) {
            EquipStarterItem(wizard, templateId);
        }

        // 2. Learn the school's first spell and stock the deck with three copies.
        var deckId = wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Deck)?.m_globalID ?? 0UL;
        if (deckId != 0UL
            && s_starterSpell.TryGetValue(wizard.MagicSchoolBehavior.MagicSchool, out var spellName)) {
            var spell = SpellFactory.GetSpell(spellName);
            if (spell is not null && wizard.LearnSpell(spell)) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) spell.m_templateID });

                for (var i = 0; i < 3; i++) {
                    var added = wizard.AddSpellToDeck(spell.m_templateID, deckId);
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK {
                        SpellID = (int) spell.m_templateID,
                        DeckID = deckId,
                        Success = (byte) (added ? 1 : 0),
                    });
                }
            }
        }
        else if (deckId == 0UL) {
            Logger.Warning("Starter kit: no deck could be equipped for {0}; spells not stocked.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));
        }

        // 3. Award the XP the tutorial would have granted (stays level 1 — it doesn't level up),
        //    and notify the client so it shows on this first login (not just after a relog).
        var oldXp = wizard.MagicSchoolBehavior.ExperiencePoints;
        wizard.AddExperiencePoints(TUTORIAL_XP_REWARD);
        var gameObject = GetActiveGameObject();
        if (gameObject is not null) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEXP {
                GlobalID = gameObject.m_globalID,
                XP = wizard.MagicSchoolBehavior.ExperiencePoints,
                OldXP = oldXp,
            });
        }

        Logger.Information("Granted starter kit (deck/wand/gear + first spell + {0} xp) to {1}.",
            Logger.Args(TUTORIAL_XP_REWARD, wizard.PlayerNameBehavior.GetWizardName()));
    }

    /// <summary>
    /// Equips a starter item the wizard already holds in inventory (by template ID), moving it
    /// into its proper slot and notifying the client. No-op if the item isn't present.
    /// </summary>
    private void EquipStarterItem(Wizard wizard, ulong templateId) {
        var item = wizard.InventoryBehavior.Items.FirstOrDefault(i => i.m_templateID == templateId);
        if (item is null) {
            return;
        }

        var template = ItemHelper.GetItemTemplate(item);
        var slot = ItemHelper.GetItemSlot(template);

        if (!wizard.InventoryToEquipmentTransfer(item.m_globalID, out _, out _)) {
            Logger.Warning("Starter kit: failed to equip item {0} (template {1}).",
                Logger.Args(item.m_globalID, templateId));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = item.m_globalID,
            SlotName = slot.SlotType.ToString(),
            IsEquip = 1,
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND))]
    private void ReceiveServerTutorialCommand(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND msg) {
        // The tutorial is handled through a lua script on the client side, which sends commands to the server
        // to force complete certain quests/goals. We need to respond to those commands here.

        // For starters, we only want to allow tutorial commands if they are in the tutorial area 
        // and have not completed the tutorial quest yet.
        var wizard = GetActiveWizard();
        if (   wizard.Zone.Contains(TUTORIAL_EXTERIOR_ZONE_NAME_CONTENTS) 
            || wizard.Zone.Contains(TUTORIAL_INTERIOR_ZONE_NAME_CONTENTS)) {
            return;
        }
        if (wizard.HasCompletedQuest(TUTORIAL_QUEST_NAME)) {
            return;
        }

        var playerObj = GetActiveGameObject();

        // Dispatch the command based off type.
        // Let GoalToComplete take priority over everything else, because sometimes the client will use both
        // the QuestToAdd and GoalToComplete in the same message.
        var commandSuccess = false;
        if (msg.GoalToComplete != string.Empty) {
            commandSuccess = HandleCommandCompleteGoal(wizard, playerObj, msg.GoalToComplete);
        }
        else if (msg.QuestToAdd != string.Empty) {
            commandSuccess = HandleCommandAddQuest(wizard, msg.QuestToAdd);
        }
        else if (msg.QuestToRemove != string.Empty) {
            commandSuccess = HandleCommandRemoveQuest(wizard, msg.QuestToRemove);
        }
        else if (msg.EventToPost != string.Empty) {
            commandSuccess = HandleCommandPostEvent(msg.EventToPost);
        }
        else if (msg.Action != string.Empty) {
            commandSuccess = HandleCommandAction(msg.Action);
        }

        if (!commandSuccess) {
            Logger.Error("Failed to process tutorial command:"
                + "QuestToAdd='{0}' "
                + "QuestToRemove='{1}' "
                + "GoalToComplete='{2}' "
                + "EventToPost='{3}' "
                + "ActionToPerform='{4}' ",
                Logger.Args(
                    msg.QuestToAdd,
                    msg.QuestToRemove,
                    msg.GoalToComplete,
                    msg.EventToPost,
                    msg.Action
                ));
        }
        else {
            Logger.Debug("Processed tutorial command successfully:"
                + "QuestToAdd='{0}', "
                + "QuestToRemove='{1}' "
                + "GoalToComplete='{2}' "
                + "EventToPost='{3}' "
                + "ActionToPerform='{4}' ",
                Logger.Args(
                    msg.QuestToAdd,
                    msg.QuestToRemove,
                    msg.GoalToComplete,
                    msg.EventToPost,
                    msg.Action
                ));
        }

        return;
    }

    private static bool HandleCommandAddQuest(Wizard wizard, string questName) {
        var questTemplate = QuestTemplateCollection.GetQuestByName(questName);
        if (questTemplate == null) {
            Logger.Error("Tutorial quest template not found");

            return false;
        }

        // Create a new quest instance.
        var qInstance = new QuestInstance(questTemplate, wizard.CharId);
        var addSuccess = wizard.AddQuest(qInstance);

        return addSuccess;
    }

    private static bool HandleCommandRemoveQuest(Wizard wizard, string questName) {
        // The client's lua defensively removes a quest right before (re)adding it, as a reset.
        // On a fresh player the quest usually isn't present yet — and "not present" is exactly
        // the state the remove is asking for, so treat it as success rather than an error.
        var hasQuest = wizard.QuestBehavior.CurrentQuestInstances
            .Any(q => q.QuestName == questName);
        if (!hasQuest) {
            return true;
        }

        return wizard.RemoveQuest(questName);
    }

    /// <summary>
    /// Handles a tutorial "action" command. The client notifies the server of tutorial-stage
    /// transitions via the "Stage" action; the server is a passive ledger (the lua drives the
    /// sequence) and the live server sends no reply, so we simply advance our stage counter and
    /// acknowledge. Any other action is acknowledged too — these are client-driven notifications,
    /// not requests we validate.
    /// </summary>
    private bool HandleCommandAction(string action) {
        if (action.Equals("Stage", System.StringComparison.OrdinalIgnoreCase)) {
            _tutorialInfo.m_tutorialStage++;

            return true;
        }

        Logger.Debug("Unhandled tutorial action '{0}'; acknowledging.", Logger.Args(action));

        return true;
    }

    private bool HandleCommandCompleteGoal(Wizard wizard,
                                           CoreObject playerObj,
                                           string goalName) {
        // We need to find the quest that contains this goal.
        // We can search the player for quest/goal instances, as they do carry a name.
        var allWizardQuestInstances = wizard.QuestBehavior.CurrentQuestInstances;
        var goalInstance = allWizardQuestInstances
            .SelectMany(q => q.GoalProgress, (quest, goal) => new { quest, goal })
            .FirstOrDefault(x => x.goal.GoalName == goalName);

        if (goalInstance != null) {
            // We can remove it from the Wizard, but we need to post the completion events as well.
            var removeSuccess = wizard.CompleteQuestGoal(goalInstance.quest.QuestName, goalName);

            // We need the actual goal instance template to get the completion results.
            var questTemplate = QuestTemplateCollection.GetQuestByName(goalInstance.quest.QuestName);
            if (questTemplate == null) {
                Logger.Error("Tutorial quest template not found");

                return false;
            }

            // Find the goal template within the quest template.
            var goalTemplate = questTemplate.m_goals
                .FirstOrDefault(g => g.m_goalName == goalName);
            if (goalTemplate == null) {
                Logger.Error("Tutorial goal template not found");

                return false;
            }

            ResultDispatcher.ExecuteResults(
                actorContext: Context,
                results: goalTemplate.m_completeResults,
                playerRef: SessionActor.ActorRef,
                playerObj: playerObj,
                questName: goalInstance.quest.QuestName,
                goalName: goalInstance.goal.GoalName,
                zoneActor: SessionActor.GetZoneActor()
            );

            return removeSuccess;
        }

        return false;
    }

    private bool HandleCommandPostEvent(string eventName) {
        ZoneBroadcastNoPlayers(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = eventName,
            PlayerActor = SessionActor.ActorRef
        });

        return true;
    }

}