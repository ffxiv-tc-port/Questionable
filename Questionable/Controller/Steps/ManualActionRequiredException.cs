using System;
namespace Questionable.Controller.Steps;

/// <summary>
/// 任務停在「要玩家自己去做」的地方（例如乙太之光還沒開啟），不是程式出錯。
/// </summary>
/// <remarks>
/// 控制器攔到它時不印「Failed to update task」，改把 <see cref="ChatMessage"/> 印到聊天視窗，
/// 再走 StopDueToFailure（QuestController 會請塔塔露喊「需要幫忙」）。
/// </remarks>
public sealed class ManualActionRequiredException : TaskException
{
    public ManualActionRequiredException()
    {
        ChatMessage = Message;
    }

    public ManualActionRequiredException(string message)
        : base(message)
    {
        ChatMessage = message;
    }

    public ManualActionRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
        ChatMessage = message;
    }

    /// <param name="message">寫進記錄檔的原因（英文，給除錯用）。</param>
    /// <param name="chatMessage">印到聊天視窗給玩家看的說明，要寫清楚請他做什麼。</param>
    public ManualActionRequiredException(string message, string chatMessage)
        : base(message)
    {
        ChatMessage = chatMessage;
    }

    public string ChatMessage { get; }
}
