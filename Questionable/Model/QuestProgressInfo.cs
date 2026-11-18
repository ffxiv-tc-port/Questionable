using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Threading;
namespace Questionable.Model;

internal sealed class QuestProgressInfo
{
    // 契約：ToString()／Tooltip 只由建構當下的任務變數決定。主視窗每幀新建一個再 ToString，
    // 所以字串延後到第一次讀才組，且變數與上一次組過的相同就沿用（任一執行緒讀到的都是完整的一組）。
    private static FormattedVariables? s_lastFormatted;

    /// <summary>任務變數打包成的鍵（最高位元組＝長度）；<see langword="null"/>＝放不進鍵，建構時已經組好。</summary>
    private readonly ulong? _variablesKey;
    private FormattedVariables? _formatted;

    public QuestProgressInfo(QuestWork questWork)
    {
        Id = new QuestId(questWork.QuestId);
        Sequence = questWork.Sequence;
        Flags = questWork.Flags;
        Span<byte> qw = questWork.Variables; // 6 bytes
        Variables = [.. qw];
        IsHidden = questWork.IsHidden;
        ClassJob = (Job)questWork.AcceptClassJob;

        if (qw.Length <= MaxPackedVariables)
        {
            _variablesKey = Pack(qw);
        }
        else
        {
            _formatted = Format(qw, 0);
        }
    }

    private const int MaxPackedVariables = 7;

    private sealed record FormattedVariables(ulong Key, string AsString, string Tooltip);

    private static ulong Pack(ReadOnlySpan<byte> qw)
    {
        ulong key = (ulong)qw.Length << 56;
        for(int i = 0; i < qw.Length; ++i)
        {
            key |= (ulong)qw[i] << (8 * i);
        }

        return key;
    }

    private FormattedVariables GetFormatted()
    {
        if (_formatted is { } formatted)
        {
            return formatted;
        }

        ulong key = _variablesKey!.Value;
        FormattedVariables? last = Volatile.Read(ref s_lastFormatted);
        if (last == null || last.Key != key)
        {
            int length = (int)(key >> 56);
            Span<byte> qw = stackalloc byte[MaxPackedVariables];
            for(int i = 0; i < length; ++i)
            {
                qw[i] = (byte)(key >> (8 * i));
            }

            last = Format(qw[..length], key);
            Volatile.Write(ref s_lastFormatted, last);
        }

        _formatted = last;
        return last;
    }

    private static FormattedVariables Format(ReadOnlySpan<byte> qw, ulong key)
    {
        string tooltip = "";
        string repr = "";
        for(int i = 0; i < qw.Length; ++i)
        {
            byte thisbyte = qw[i];
            tooltip += $"{Convert.ToString(thisbyte, 2).PadLeft(8).Replace(" ", "0", StringComparison.InvariantCulture)}\n";
            int upper = thisbyte >> 4;
            int lower = thisbyte & 0xF;
            //repr += $"{thisbyte} ";
            if (upper != 0 || lower != 0)
            {
                repr += "(";
                if (upper != 0)
                {
                    repr += $"{upper}H";
                }
                if (lower != 0)
                {
                    repr += $"{lower}L";
                }
                repr += ") ";
            }
            else
            {
                repr += "(-) ";
            }
            if (i % 2 == 1)
            {
                repr += "   ";
            }
        }

        // For combat quests, a sequence to kill 3 enemies works a bit like this:
        // Trigger enemies → 0
        // Kill first enemy → 1
        // Kill second enemy → 2
        // Last enemy → increase sequence, reset variable to 0
        // The order in which enemies are killed doesn't seem to matter.
        // If multiple waves spawn, this continues to count up (e.g. 1 enemy from wave 1, 2 enemies from wave 2, 1 from wave 3) would count to 3 then 0
        return new FormattedVariables(key, $"QW: {repr.Trim()}", tooltip);
    }

    public ElementId Id { get; }
    public byte Sequence { get; }
    public ushort Flags { get; init; }
    public List<byte> Variables { get; }
    public bool IsHidden { get; }
    public Job ClassJob { get; }
    public string Tooltip => GetFormatted().Tooltip;

    public override string ToString()
    {
        return GetFormatted().AsString;
    }
}
