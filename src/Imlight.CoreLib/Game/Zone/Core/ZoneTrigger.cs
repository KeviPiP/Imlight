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
 * TRIGGER SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Handles event-based scripting within zones, allowing for interactive
 * elements like gates, teleporters, and scripted sequences.
 * 
 * USAGE EXAMPLE:
 * // Created by the Zone system during zone loading
 * // Trigger triggerData = ...
 * var triggerActor = Context.ActorOf(Props.Create(() => 
 *     new ZoneTrigger(zoneRef, zone, triggerData)));
 * 
 * NOTE:
 * Triggers are usually activated by volumes
 * Supports player-specific cooldowns for trigger activation.
 * Dynamically attaches result handlers based on trigger configuration.
 *
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Represents a trigger (or event) within a <see cref="Zone"/>. Triggers are used to
/// handle events such as a gate opening, zone transfer, or other scripted events.
/// </summary>
/// <param name="zoneRef">The reference to the zone that this trigger is a part of.</param>
/// <param name="zone">The zone that this trigger is a part of.</param>
public sealed class ZoneTrigger(IActorRef zoneRef, Zone zone, Trigger trigger)
    : ZoneEntity(null, null, null, zoneRef, zone) {

    // A trigger never re-fires for the same player faster than this, even when it has no
    // configured cooldown. Triggers fire on discrete player events (zone enter, proximity,
    // posted events), so a sub-second floor is invisible to normal play but stops a trigger
    // whose results are a no-op (e.g. an unhandled result type) from spinning at mailbox
    // speed and flooding the actor system.
    private const double MIN_REFIRE_INTERVAL_SECONDS = 1.0;

    // Dev/testing escape hatch: when true, triggers fire even if their requirements aren't
    // met. Opens quest-gated gates/doors on a server where quest progression isn't wired up
    // yet. Off by default — flip [Debug] BypassTriggerRequirements in the config to enable.
    private static readonly bool s_bypassTriggerRequirements =
        ConfigurationManager.GetValue("Debug.BypassTriggerRequirements", false);

    public Trigger TriggerData { get; init; } = trigger;
    private readonly Dictionary<IActorRef, DateTime> _cooldowns = [];
    private bool _loggedRequirementFailure;

    // Unsure why this override is required, but it fails without it present.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN))]
    protected override void ReceiveObjectLoadBegin() 
        => base.ReceiveObjectLoadBegin();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_POSTEVENT))]
    private void ReceivePostEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // Early-out if the event name doesn't match any configured fire events.
        if (!TriggerData.m_fireEvents.Any(x => x == message.EventName)) {
            return;
        }

        // Early-out if this trigger is still within its (floored) per-player re-fire window.
        if (!FireGuardCheck(message.PlayerActor)) {
            return;
        }

        // Evaluate requirements when present, unless the dev bypass is enabled (which opens
        // every gated trigger for testing on a server where quest progression isn't wired up).
        if (   !s_bypassTriggerRequirements
            && TriggerData.m_requirements is not null
            && TriggerData.m_requirements.m_requirements is not null
            && TriggerData.m_requirements.m_requirements.Count > 0) {
            var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
            var wizardResponse = message.PlayerActor.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryWizardMsg).Result;

            var requirementsMet = RequirementDispatcher.EvaluateRequirements(
                requirements: TriggerData.m_requirements,
                context: new ZoneRequirementContext(
                    TriggerData.m_requirements,
                    message.PlayerActor,
                    message.PlayerGameObject,
                    wizardResponse.Wizard,
                    ZoneRef,
                    TriggerData.m_triggerName
                )
            );

            if (!requirementsMet) {
                // Diagnostic: surface WHY a trigger (e.g. a gate/door) refuses to fire, once
                // per trigger instance, dumping the requirement records (ReqHasGoal etc. print
                // their quest/goal). This is how we tell a quest-gated gate apart from a bug.
                if (!_loggedRequirementFailure) {
                    _loggedRequirementFailure = true;
                    Logger.Debug("Trigger '{0}' did not fire: requirements not met. Requirements: {1}",
                        Logger.Args(TriggerData.m_triggerName,
                            string.Join("; ", TriggerData.m_requirements.m_requirements
                                .Select(r => r?.ToString() ?? "<null>"))));
                }

                return;
            }
        }

        ResultDispatcher.ExecuteResults(Context, TriggerData.m_results, message.PlayerActor, message.PlayerGameObject,
                                       Sender, ZoneRef, triggerName: TriggerData.m_triggerName);
    }

    /// <summary>
    /// Decides whether the trigger may fire for the given player right now. Honours the
    /// trigger's configured cooldown, but never lets it re-fire faster than
    /// <see cref="MIN_REFIRE_INTERVAL_SECONDS"/> so a no-op result can't spin at mailbox
    /// speed. Records the fire time when it allows a fire. Does NOT cap lifetime fires —
    /// gates/doors must keep working on every use.
    /// </summary>
    private bool FireGuardCheck(IActorRef playerRef) {
        var effectiveCooldown = Math.Max(TriggerData.m_cooldown, MIN_REFIRE_INTERVAL_SECONDS);
        if (_cooldowns.TryGetValue(playerRef, out var lastTriggered)
            && DateTime.Now - lastTriggered < TimeSpan.FromSeconds(effectiveCooldown)) {
            return false;
        }

        _cooldowns[playerRef] = DateTime.Now;

        return true;
    }


}