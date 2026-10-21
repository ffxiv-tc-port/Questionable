using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Questing;
using System;
namespace Questionable.Controller.Steps.Interactions;

internal static class Aetheryte
{
    internal sealed class Factory : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.AttuneAetheryte)
            {
                return null;
            }

            ArgumentNullException.ThrowIfNull(step.Aetheryte);

            return new Attune(step.Aetheryte.Value);
        }
    }

    internal sealed record Attune(EAetheryteLocation AetheryteLocation) : ITask
    {
        public bool ShouldRedoOnInterrupt()
        {
            return true;
        }
        public override string ToString()
        {
            return $"AttuneAetheryte({AetheryteLocation})";
        }
    }

    internal sealed class DoAttune
    (
        AetheryteFunctions aetheryteFunctions,
        GameFunctions gameFunctions,
        ICondition condition,
        ILogger<DoAttune> logger) : TaskExecutor<Attune>
    {
        private readonly InteractionRetry _retry = new();

        protected override bool Start()
        {
            if (!aetheryteFunctions.IsAetheryteUnlocked(Task.AetheryteLocation))
            {
                logger.LogInformation("Attuning to aetheryte {Aetheryte}", Task.AetheryteLocation);
                return true;
            }

            logger.LogInformation("Already attuned to aetheryte {Aetheryte}", Task.AetheryteLocation);
            return false;
        }

        public override ETaskResult Update()
        {
            if (aetheryteFunctions.IsAetheryteUnlocked(Task.AetheryteLocation))
            {
                return ETaskResult.TaskComplete;
            }

            if (_retry.ShouldSend(condition, logger, Task))
            {
                bool sent = false;
                ProgressContext = InteractionProgressContext.FromActionUseOrDefault(() =>
                    sent = gameFunctions.InteractWith((uint)Task.AetheryteLocation, ObjectKind.Aetheryte));
                _retry.OnTried(sent);
            }

            return ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return true;
        }
    }
}
