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
    private readonly ProcessChatBoxDelegate _processChatBox =
        Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(UIModule.Addresses.ProcessChatBoxEntry.Value);
    private readonly ITargetManager _targetManager = targetManager;

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
        // Framework.Instance() 宣告成 [StaticAddress("48 8B 1D ...", 3, isPointer: true)]：產生器讀的是
        // 「指標的位址」再多解參考一層，所以它真的會回 null(登入前、登出後、關閉中都是常態)；
        // 只有特徵碼失配時才改成擲例外。不帶 isPointer 的那種才是「null 就擲、否則保證非 null」，
        // 光看 attribute 名稱分不出來，必須看有沒有 isPointer。
        // GetUIModule() 是 [MemberFunction] 原生呼叫：對 null 的 this 呼叫會在遊戲碼裡解參考，
        // 得到 AccessViolationException —— 在 .NET Core 屬 corrupted-state exception，
        // try/catch 完全攔不到 ⇒ 只能事前判空。就算僥倖回來，_processChatBox 也會拿著假的
        // uiModule 再崩一次。
        // 本方法的 XML 文件既有慣例就是前提不成立時擲 InvalidOperationException，這裡沿用：
        // 訊息不送出，呼叫端拿到的是可攔截的受管理例外，而不是整個遊戲閃退。
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

        using ChatPayload payload = new(message);
        nint mem1 = Marshal.AllocHGlobal(400);
        Marshal.StructureToPtr(payload, mem1, false);

        _processChatBox(uiModule, mem1, nint.Zero, 0);

        Marshal.FreeHGlobal(mem1);
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
    }

    public void UseEmote(EEmote emote)
    {
        ExecuteCommand($"{_emoteCommands[emote]} motion");
    }
    private delegate void ProcessChatBoxDelegate(nint uiModule, nint message, nint unused, byte a4);

    [StructLayout(LayoutKind.Explicit)]
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
    private readonly struct ChatPayload : IDisposable
    {
        [FieldOffset(0)] private readonly IntPtr textPtr;

        [FieldOffset(16)] private readonly ulong textLen;

        [FieldOffset(8)] private readonly ulong unk1;

        [FieldOffset(24)] private readonly ulong unk2;

        internal ChatPayload(byte[] stringBytes)
        {
            textPtr = Marshal.AllocHGlobal(stringBytes.Length + 30);
            Marshal.Copy(stringBytes, 0, textPtr, stringBytes.Length);
            Marshal.WriteByte(textPtr + stringBytes.Length, 0);

            textLen = (ulong)(stringBytes.Length + 1);

            unk1 = 64;
            unk2 = 0;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(textPtr);
        }
    }
}
