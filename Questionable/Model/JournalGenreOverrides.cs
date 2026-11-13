namespace Questionable.Model;

/// <remarks><see langword="null"/>＝用來取分類的參照任務在 Quest 表裡不存在，該組任務改用自己的分類。</remarks>
internal sealed class JournalGenreOverrides
{
    public required uint? ARelicRebornQuests { get; init; }
    public required uint? ThavnairSideQuests { get; init; }
    public required uint? RadzAtHanSideQuests { get; init; }
}
