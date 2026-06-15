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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results;

/// <summary>
/// Actor responsible for executing results sequentially for a single activation instance.
/// Creates handler actors, executes results one at a time, then self-destructs when complete.
/// </summary>
public class ResultExecutorActor(IResultContext context) : ReceiveProtocolDispatcher, IWithTimers {

    private const uint RESULT_HANDLER_TIMEOUT_MS = 30000;

    // Result types we've already warned about having no handler. A missing handler can be
    // hit thousands of times per second (e.g. a trigger that re-fires every tick), so we
    // log each unhandled type only once to avoid flooding the log.
    private static readonly ConcurrentDictionary<string, byte> s_warnedMissingHandlers = new();

    private readonly IResultContext _context = context;
    private readonly Queue<Result> _resultQueue = new();
    private bool _isExecuting;
    private bool _allSuccessful = true;
    private IActorRef _replyTo;

    public ITimerScheduler Timers { get; set; }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_EXECUTERESULTS))]
    private void HandleExecuteResults(CHARACTER_103_PROTOCOL.MSG_EXECUTERESULTS message) {
        _replyTo = _context.GetReplyTo();

        var results = _context.GetResults();
        if (results == null || !results.Any()) {
            SendFinalReply(true);
            return;
        }

        var resultsList = results.Where(r => r != null).ToList();
        if (resultsList.Count == 0) {
            SendFinalReply(true);
            return;
        }

        foreach (var result in resultsList) {
            _resultQueue.Enqueue(result);
        }

        ProcessNextResult();
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_RESULTHANDLERCOMPLETED))]
    private void ReceiveResultHandlerCompleted(CHARACTER_103_PROTOCOL.MSG_RESULTHANDLERCOMPLETED message) {
        _isExecuting = false;

        if (!message.Success) {
            _allSuccessful = false;
            Logger.Warning("Result handler for type {0} reported failure.",
                Logger.Args(message.ResultType.Name));
        } 

        ProcessNextResult();
    }

    private void ProcessNextResult() {
        if (_isExecuting) {
            return;
        }

        if (_resultQueue.Count == 0) {
            SendFinalReply(_allSuccessful);
            return;
        }

        var result = _resultQueue.Dequeue();
        var resultType = result.GetType();

        // Special case: ResWait doesn't need a handler, just schedule a delay.
        if (result is ResWait resWait) {
            _isExecuting = true;

            Timers.StartSingleTimer(
                key: $"ResWait_{Guid.NewGuid():N}",
                msg: new CHARACTER_103_PROTOCOL.MSG_RESULTHANDLERCOMPLETED {
                    Success = true,
                    ResultType = resultType
                },
                timeout: TimeSpan.FromMilliseconds(resWait.m_secondsToWait * 1000)
            );

            return;
        }

        var handlerType = ResultDispatcher.FindHandlerForResult(resultType, _context);

        if (handlerType is null) {
            // Dump the result's full contents (it's a generated record, so ToString prints
            // every property) and the context it came from, so an unhandled result tells us
            // exactly what fired it and where. Logged once per type to avoid flooding.
            if (s_warnedMissingHandlers.TryAdd(resultType.Name, 0)) {
                Logger.Warning("No handler registered for result type: {0} | result: {1} | context: {2} " +
                    "(further occurrences suppressed)",
                    Logger.Args(resultType.Name, result, DescribeContext(_context)));
            }

            ProcessNextResult();

            return;
        }

        try {
            _isExecuting = true;

            // Create the handler actor, tell them to initialize with the given context, then execute.
            var props = Props.Create(handlerType);
            var handlerActor = Context.ActorOf(props, $"Handler_{resultType.Name}_{Guid.NewGuid():N}");

            handlerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_INITIALIZEHANDLER { Context = _context, Result = result });
            var executeMessage = new CHARACTER_103_PROTOCOL.MSG_EXECUTEHANDLER();
            var executeTimeout = TimeSpan.FromMilliseconds(RESULT_HANDLER_TIMEOUT_MS);

            handlerActor.Ask<CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED>(executeMessage, executeTimeout)
                .ContinueWith(t => new CHARACTER_103_PROTOCOL.MSG_RESULTHANDLERCOMPLETED {
                    Success = t.IsCompletedSuccessfully && t.Result?.Success == true,
                    ResultType = resultType
                })
                .PipeTo(Self);
        }
        catch (Exception ex) {
            _isExecuting = false;
            _allSuccessful = false;
            Logger.Error("Failed to create/execute handler for result type {0}: {1}",
                Logger.Args(resultType.Name, ex.Message));

            ProcessNextResult();
        }
    }

    /// <summary>
    /// Builds a short, human-readable description of the context a result was executed in
    /// (the trigger / quest / goal it belongs to, and which player it ran for), for
    /// diagnostic logging of unhandled result types.
    /// </summary>
    private static string DescribeContext(IResultContext context) {
        if (context is null) {
            return "<none>";
        }

        var parts = new List<string>();
        switch (context) {
            case ZoneResultContext zone when zone.Trigger?.TriggerData is not null:
                parts.Add($"trigger={zone.Trigger.TriggerData.m_triggerName}");
                break;
            case QuestResultContext quest:
                if (!string.IsNullOrEmpty(quest.QuestName)) {
                    parts.Add($"quest={quest.QuestName}");
                }
                if (!string.IsNullOrEmpty(quest.GoalName)) {
                    parts.Add($"goal={quest.GoalName}");
                }
                break;
            case GenericResultContext generic:
                if (!string.IsNullOrEmpty(generic.QuestName)) {
                    parts.Add($"quest={generic.QuestName}");
                }
                if (!string.IsNullOrEmpty(generic.GoalName)) {
                    parts.Add($"goal={generic.GoalName}");
                }
                if (!string.IsNullOrEmpty(generic.TriggerName)) {
                    parts.Add($"trigger={generic.TriggerName}");
                }
                break;
        }

        var playerObj = context.GetPlayerObj();
        if (playerObj is not null) {
            parts.Add($"player={playerObj.m_globalID.Full}");
        }

        return parts.Count == 0 ? context.GetType().Name : string.Join(", ", parts);
    }

    private void SendFinalReply(bool success) {
        _replyTo?.Tell(new CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED { Success = success });
        Context.Stop(Self);
    }

}