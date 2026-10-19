using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
namespace Questionable.Functions;

internal sealed unsafe class ChatFunctions
(
    IDataManager dataManager,
    GameFunctions gameFunctions,
    ITargetManager targetManager,
    ILogger<ChatFunctions> logger)
{
    private readonly ReadOnlyDictionary<EEmote, string> _emoteCommands = dataManager.GetExcelSheet<Emote>()
        .Where(x => x.RowId > 0)
        .Where(x => x.TextCommand.IsValid)
        .Select(x => (x.RowId, Command: x.TextCommand.Value.Command.ToString()))
        .Where(x => !string.IsNullOrEmpty(x.Command) && x.Command.StartsWith('/'))
        .ToDictionary(x => (EEmote)x.RowId, x => x.Command)
        .AsReadOnly();

    private readonly GameFunctions _gameFunctions = gameFunctions;
    private readonly ILogger<ChatFunctions> _logger = logger;
    private readonly ProcessChatBoxDelegate? _processChatBox = ResolveProcessChatBox(logger);
    private readonly ITargetManager _targetManager = targetManager;

    /// <summary>false＝遊戲改版後送聊天指令的函式位址解不出：任何指令與情感動作都送不出去。</summary>
    public bool IsAvailable => _processChatBox != null;

    // 位址是 0 時 GetDelegateForFunctionPointer 會擲例外，而這個類別在載入時就由 DI 建立＝整個插件載入失敗。
    private static ProcessChatBoxDelegate? ResolveProcessChatBox(ILogger logger)
    {
        nint address = (nint)UIModule.Addresses.ProcessChatBoxEntry.Value;
        if (address == 0)
        {
            logger.LogWarning("遊戲改版後 UIModule.ProcessChatBoxEntry 的位址對不上：送不出聊天指令與情感動作（需要說話或做動作的任務步驟會停下並提示），其餘功能照常。");
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(address);
    }

    /// <summary>
    ///     <para>
    ///         Send a given message to the chat box. <b>This can send chat to the server.</b>
    ///     </para>
    ///     <para>
    ///         <b>This method is unsafe.</b> This method does no checking on your input and
    ///         may send content to the server that the normal client could not. You must
    ///         verify what you're sending and handle content and length to properly use
    ///         this.
    ///     </para>
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <exception cref="InvalidOperationException">If the signature for this function could not be found</exception>
    private void SendMessageUnsafe(byte[] message)
    {
        if (_processChatBox == null)
        {
            throw new InvalidOperationException("ProcessChatBoxEntry is not available (game updated?); chat message was not sent.");
        }

        // Framework.Instance() 是 isPointer: true 的 [StaticAddress]：真的會回 null（登入前、登出後、關閉中都是常態），
        // 只有特徵碼失配時才擲例外。GetUIModule() 對 null 的 this 呼叫是 try/catch 攔不到的 AccessViolation
        // ⇒ 只能事前判空。
        // 沿用本方法 XML 文件的慣例，前提不成立時擲 InvalidOperationException：訊息不送出，
        // 呼叫端拿到的是可攔截的受管理例外，而不是整個遊戲閃退。
        var framework = Framework.Instance();
        if (framework == null)
        {
            throw new InvalidOperationException("Framework is not available; chat message was not sent.");
        }

        var uiModulePtr = framework->GetUIModule();
        if (uiModulePtr == null)
        {
            throw new InvalidOperationException("UIModule is not available; chat message was not sent.");
        }

        nint uiModule = (nint)uiModulePtr;

        // 遊戲要的是完整初始化的 Utf8String（0x68）；字串內容到第一個 NUL 為止，與原生 C 字串讀法相同。
        byte[] terminated = new byte[message.Length + 1];
        message.CopyTo(terminated, 0);
        Utf8String* text;
        fixed (byte* bytes = terminated)
        {
            text = Utf8String.FromSequence(bytes);
        }

        try
        {
            _processChatBox(uiModule, (nint)text, nint.Zero, 0);
        }
        finally
        {
            text->Dtor();
            IMemorySpace.Free(text);
        }
    }

    /// <summary>
    ///     <para>
    ///         Send a given message to the chat box. <b>This can send chat to the server.</b>
    ///     </para>
    ///     <para>
    ///         This method is slightly less unsafe than <see cref="SendMessageUnsafe" />. It
    ///         will throw exceptions for certain inputs that the client can't normally send,
    ///         but it is still possible to make mistakes. Use with caution.
    ///     </para>
    /// </summary>
    /// <param name="message">message to send</param>
    /// <exception cref="ArgumentException">
    ///     If <paramref name="message" /> is empty, longer than 500 bytes in UTF-8, or
    ///     contains invalid characters.
    /// </exception>
    /// <exception cref="InvalidOperationException">If the signature for this function could not be found</exception>
    private void SendMessage(string message)
    {
        _logger.LogDebug("Attempting to send chat message '{Message}'", message);
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("message is empty", nameof(message));
        }

        if (bytes.Length > 500)
        {
            throw new ArgumentException("message is longer than 500 bytes", nameof(message));
        }

        if (message.Length != SanitiseText(message).Length)
        {
            throw new ArgumentException("message contained invalid characters", nameof(message));
        }

        SendMessageUnsafe(bytes);
    }

    /// <summary>
    ///     <para>
    ///         Sanitises a string by removing any invalid input.
    ///     </para>
    ///     <para>
    ///         The result of this method is safe to use with
    ///         <see cref="SendMessage" />, provided that it is not empty or too
    ///         long.
    ///     </para>
    /// </summary>
    /// <param name="text">text to sanitise</param>
    /// <returns>sanitised text</returns>
    /// <exception cref="InvalidOperationException">If the signature for this function could not be found</exception>
    private string SanitiseText(string text)
    {
        Utf8String* uText = Utf8String.FromString(text);

        uText->SanitizeString((AllowedEntities)0x27F);
        string sanitised = uText->ToString();

        uText->Dtor();
        IMemorySpace.Free(uText);

        return sanitised;
    }

    public void ExecuteCommand(string command)
    {
        if (!command.StartsWith('/'))
        {
            return;
        }

        SendMessage(command);
    }

    public void UseEmote(uint dataId, EEmote emote)
    {
        IGameObject? gameObject = _gameFunctions.FindObjectByDataId(dataId);
        if (gameObject != null)
        {
            _targetManager.Target = gameObject;
            ExecuteCommand($"{_emoteCommands[emote]} motion");
        }
        else
        {
            _logger.LogWarning("Not using emote {Emote}: no object with dataId {DataId}", emote, dataId);
        }
    }

    public void UseEmote(EEmote emote)
    {
        ExecuteCommand($"{_emoteCommands[emote]} motion");
    }
    private delegate void ProcessChatBoxDelegate(nint uiModule, nint message, nint unused, byte a4);
}
