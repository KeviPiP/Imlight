using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.Services;

internal class DebugService(SessionActor sessionActor) : MessageService(sessionActor) {
    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new QuestService(parentActor));

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void GetDefeatedModAdjectives(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        Logger.Debug("MobAdjectives '{0}'", message.MobAdjectives);
        
    }
}
