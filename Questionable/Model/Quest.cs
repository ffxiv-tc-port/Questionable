using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
namespace Questionable.Model;

internal sealed class Quest
{
    public enum ESource
    {
        Assembly,
        ProjectDirectory,
        UserDirectory
    }

    public required ElementId Id { get; init; }
    public required QuestRoot Root { get; init; }
    public required IQuestInfo Info { get; init; }
    public required ESource Source { get; init; }

    /// <summary>與 <c>SingleOrDefault(seq =&gt; seq.Sequence == currentSequence)</c> 同語意（重複就擲），但不配置。</summary>
    /// <remarks>主視窗與任務迴圈每幀都會呼叫；捕獲參數的 lambda 每次都配置委派＋閉包。</remarks>
    public QuestSequence? FindSequence(byte currentSequence)
    {
        List<QuestSequence> sequences = Root.QuestSequence;
        ArgumentNullException.ThrowIfNull(sequences, "source");

        QuestSequence? found = null;
        bool hasMatch = false;
        foreach(QuestSequence seq in CollectionsMarshal.AsSpan(sequences))
        {
            if (seq.Sequence != currentSequence)
            {
                continue;
            }

            if (hasMatch)
            {
                throw new InvalidOperationException("Sequence contains more than one matching element");
            }

            found = seq;
            hasMatch = true;
        }

        return found;
    }

    public IEnumerable<QuestSequence> AllSequences()
    {
        return Root.QuestSequence;
    }

    public IEnumerable<(QuestSequence Sequence, int StepId, QuestStep Step)> AllSteps()
    {
        foreach(QuestSequence sequence in Root.QuestSequence)
        {
            for(int i = 0; i < sequence.Steps.Count; ++i)
            {
                QuestStep step = sequence.Steps[i];
                yield return (sequence, i, step);
            }
        }
    }
}
