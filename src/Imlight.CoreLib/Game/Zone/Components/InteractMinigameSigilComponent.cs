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
 * INTERACT MINIGAME SIGIL COMPONENT  (key bosses / silver-chest / dungeon sigils)
 * ========================================================================
 *
 * Reconstructed from packet captures (docs/sigil-woodkey-boss-flow.md). A
 * "minigame" sigil (skeleton/wooden-key boss, silver chest, tower entry) is a
 * quest-gated, instanced dungeon entry:
 *
 *   interact (ServiceName "MinigameSigilService")
 *     -> server checks SigilTemplate.m_requirements (have/completed quest)
 *     -> MSG_AGGRO (snap player onto the sigil) + MSG_MINIGAMETIMERSTART(Time=10, Teleport=1)
 *     -> after 10s -> run SigilTemplate.m_entryResults via ResultDispatcher
 *        (the ResTeleport sets OwnerCharId -> GameWorld routes to a per-owner
 *         private instance; no new instancing code, reuses the container path).
 *
 * Reuses: RequirementDispatcher (quest gate), ResultDispatcher (entry/transfer),
 * SigilFactory (template), IWithTimers (the 10s fill).
 *
 * NOTE: untested against a live client. Runtime-verify points are flagged TODO.
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractMinigameSigilComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    private const string SERVICE_NAME = "MinigameSigilService";
    private const float SIGIL_FILL_SECONDS = 10f;
    private const string FILL_TIMER_KEY = "minigame_sigil_fill";

    public ITimerScheduler Timers { get; set; }

    // The sigil template (requirements + entry results) — obtained from the sigil
    // details on zone load, exactly like CombatDuelComponent.
    private SigilTemplate _sigilTemplate;
    private ulong SigilGid => Entity.ActiveGameObject.m_globalID;

    // ---- IServiceComponent surface ----
    public string ServiceName => SERVICE_NAME;
    public string NpcIcon => "";
    public string NpcNameKey => null;
    public string NpcTextKey => null;
    public string StateName => "";
    public string InteractWizBang => "";
    public string DisplayKey => "";
    public WizBangs WizBang => WizBangs.None;

    // Attach to sigil objects that are NOT combat duels (combat sigils carry a
    // "DuelBehavior" and are handled by CombatDuelComponent). Minigame/dungeon
    // sigils carry a sigil behavior but no duel behavior.
    // TODO(verify): confirm the exact behavior name minigame sigils carry; the
    // capture shows they use the "MinigameSigilService" interaction path.
    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go
        && go.m_behaviors is not null
        && go.m_behaviors.Any(b => b is not null && b.m_behaviorName is not null
            && b.m_behaviorName.Contains("Sigil"))
        && !go.m_behaviors.Any(b => b is not null && b.m_behaviorName == "DuelBehavior");

    // Sigil details (carries m_sigilType -> SigilFactory template). Same handler
    // CombatDuelComponent uses; if minigame sigils flow through a different
    // message, this is the runtime-verify point.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILDETAILS))]
    private void ReceiveSigilDetails(ZONE_102_PROTOCOL.MSG_SIGILDETAILS message) {
        var sigilType = message.CombatSigilObjectInfo?.m_sigilType;
        if (string.IsNullOrEmpty(sigilType)) {
            return;
        }
        _sigilTemplate = SigilFactory.GetSigilTemplate(sigilType);
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (!IsMinigameSigil() || !RequirementsMet(wizard)) {
            yield break;
        }

        yield return new MinigameSigilOption {
            m_serviceName = SERVICE_NAME,
        };
    }

    public void OnServiceInteraction(IActorRef playerActor,
                                     Wizard playerCharacter,
                                     CoreObject playerObject,
                                     uint serviceOptionIndex) {
        if (!IsMinigameSigil() || !RequirementsMet(playerCharacter)) {
            Logger.Debug("Minigame sigil interaction rejected (no template/reqs not met).");
            return;
        }

        // 1) Snap the player onto the sigil.
        var sigilLoc = Entity.ActiveGameObject.m_location;
        var playerLoc = playerObject.m_location;
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_AGGRO {
            GlobalID = playerObject.m_globalID,
            LocX = playerLoc.X, LocY = playerLoc.Y, LocZ = playerLoc.Z, Yaw = 0f,
            SigilGID = SigilGid,
            SigilX = sigilLoc.X, SigilY = sigilLoc.Y, SigilZ = sigilLoc.Z, SigilYaw = 0f,
        });

        // 2) Start the client-side 10s fill countdown (Teleport=1 -> client expects a transfer).
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMERSTART {
            Time = SIGIL_FILL_SECONDS,
            SigilGID = SigilGid,
            Teleport = 1,
        });

        // 3) After the fill completes, run the sigil's entry results (the transfer).
        Timers.StartSingleTimer(
            FILL_TIMER_KEY,
            new MinigameSigilFillComplete(playerActor, playerObject),
            System.TimeSpan.FromSeconds(SIGIL_FILL_SECONDS));
    }

    [MessageHandler(typeof(MinigameSigilFillComplete))]
    private void OnFillComplete(MinigameSigilFillComplete msg) {
        if (_sigilTemplate?.m_entryResults?.m_results is not { Count: > 0 }) {
            Logger.Warning("Minigame sigil {0} has no entry results to execute.", Logger.Args(SigilGid));
            return;
        }

        // The entry ResultList contains a ResTeleport whose handler sets
        // OwnerCharId -> GameWorld routes to a per-owner private instance.
        ResultDispatcher.ExecuteResults(
            actorContext: Context,
            results: _sigilTemplate.m_entryResults,
            playerRef: msg.PlayerRef,
            playerObj: msg.PlayerObj,
            zoneActor: ZoneActor);
    }

    private bool IsMinigameSigil()
        => _sigilTemplate?.m_entryResults?.m_results is { Count: > 0 };

    private bool RequirementsMet(Wizard wizard) {
        if (_sigilTemplate?.m_requirements is null) {
            return true;   // ungated sigil
        }

        return RequirementDispatcher.EvaluateRequirements(
            _sigilTemplate.m_requirements,
            new GenericRequirementContext(_sigilTemplate.m_requirements, null, null, wizard));
    }

    // Internal self-message: the sigil fill timer elapsed; carry who is entering.
    // (Never serialized to the wire; order/service are nominal.)
    private sealed class MinigameSigilFillComplete(IActorRef playerRef, CoreObject playerObj) : IServerMessage {
        public byte MessageOrder { get; } = 0;
        public byte ServiceID { get; } = 0;
        public IActorRef PlayerRef { get; } = playerRef;
        public CoreObject PlayerObj { get; } = playerObj;
    }

}
