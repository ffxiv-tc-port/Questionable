using Dalamud.Plugin.Services;
using System.Threading.Tasks;

namespace Questionable.Utils;

/// <summary>
/// 把聊天輸出釘在 framework 執行緒上。
/// </summary>
/// <remarks>
/// 🔴 <b>本 pin 的 <c>ChatGui</c> 內部是一個沒有任何同步的 <c>Queue&lt;XivChatEntry&gt;</c></b>
/// （<c>Dalamud/Game/Gui/ChatGui.cs:43</c>）：<c>Print</c>／<c>PrintError</c> 只是 <c>Enqueue</c>，
/// 而 <c>UpdateQueue()</c> 在 framework 執行緒上 <c>TryDequeue</c>
/// （<c>Dalamud/Game/Framework.cs:393</c> 每幀呼叫一次）。
/// <para>
/// 🔴 <b>IPC 端點跑在呼叫端外掛的執行緒上</b>，所以「從 IPC 端點可達的聊天輸出」＝
/// 另一條執行緒在 <c>Enqueue</c>、framework 執行緒同時在 <c>TryDequeue</c>。
/// <c>Queue&lt;T&gt;</c> 的失敗形式不是「少一則訊息」而是<b>佇列本身壞掉</b>——
/// <c>Enqueue</c> 滿了會 <c>Grow()</c> 換掉底層陣列，head／tail／size 也可能撕裂。
/// </para>
/// <para>
/// 📌 <c>IFramework.RunOnFrameworkThread(Action)</c> <b>已經在 framework 執行緒上時就地同步執行</b>
/// （<c>Framework.cs:173-181</c>），所以每幀那條正常路徑的行為完全沒有變；
/// 只有真的從別的執行緒進來的呼叫才會被排到下一幀。
/// </para>
/// </remarks>
internal static class ChatGuiExtensions
{
    /// <summary>
    /// 與 <c>IChatGui.PrintError(string, string?, ushort?)</c> 相同，但保證在 framework 執行緒上寫入。
    /// </summary>
    public static void PrintErrorOnFrameworkThread(this IChatGui chatGui, IFramework framework,
        string message, string? messageTag = null, ushort? tagColor = null)
    {
        Task task = framework.RunOnFrameworkThread(() => chatGui.PrintError(message, messageTag, tagColor));

        // 就地執行那條路徑上，Dalamud 把例外收進回傳的 Task 而不是往外擲（Framework.cs:180）。
        // 這裡把它擲回去，行為才真的與「直接呼叫 PrintError」一模一樣。
        // 排隊那條路徑的 Task 這時還沒完成，IsFaulted 是 false，不受影響。
        if (task.IsFaulted)
        {
            task.GetAwaiter().GetResult();
        }
    }
}
