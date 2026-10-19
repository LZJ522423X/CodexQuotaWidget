using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaWidget.Core.Observation;

/// <summary>
/// Only an output journal of observed state changes, not a complete native event stream.
/// Sequence numbers belong to one data-root writer and do not prove task stopping.
/// The owning event store supplies the cross-process commit lease and event validation.
/// </summary>
internal sealed class CodexStateTimelineStore(string path, Action<CodexStateChangedEvent> validateEvent)
{
    public const int MaximumEntries = 256;
    const long MaximumBytes = 4 * 1024 * 1024;

    sealed record TimelineDocument
    {
        [JsonRequired] public int SchemaVersion { get; init; } = 1;
        [JsonRequired] public int Capacity { get; init; } = MaximumEntries;
        [JsonRequired] public bool PassiveHistory { get; init; } = true;
        [JsonRequired] public bool GrantsActionAuthority { get; init; }
        [JsonRequired] public CodexStateChangedEvent[] Entries { get; init; } = [];
    }

    public IReadOnlyList<CodexStateChangedEvent> Read()
    {
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak"))
                throw new InvalidDataException("状态历史主记录缺失但备份仍存在，未回退或覆盖");
            return [];
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length is < 1 or > MaximumBytes) throw new InvalidDataException("状态历史大小异常，未覆盖");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.Length != bytes.Length) throw new InvalidDataException("读取期间状态历史改变，未覆盖");
        var document = JsonSerializer.Deserialize<TimelineDocument>(bytes,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("状态历史为空");
        if (document.SchemaVersion != 1 || document.Capacity != MaximumEntries || !document.PassiveHistory
            || document.GrantsActionAuthority || document.Entries is null || document.Entries.Length > MaximumEntries)
            throw new InvalidDataException("状态历史结构异常或试图授予动作权限");
        // Required schema-2 fields are conditional and cannot be expressed by JsonRequired alone.
        using var json = JsonDocument.Parse(bytes);
        CodexStateChangedEventStore.RejectDuplicateProperties(json.RootElement);
        var rawEntries = json.RootElement.GetProperty(nameof(TimelineDocument.Entries));
        foreach (var entry in rawEntries.EnumerateArray())
            CodexStateChangedEventStore.EnsureVersionedFields(Encoding.UTF8.GetBytes(entry.GetRawText()));
        var previous = 0L;
        var identities = new HashSet<Guid>();
        foreach (var entry in document.Entries)
        {
            if (entry is null) throw new InvalidDataException("状态历史含空条目");
            validateEvent(entry);
            if (entry.SchemaVersion != 2 || entry.SequenceNumber <= previous || !identities.Add(entry.EventId))
                throw new InvalidDataException("状态历史序号倒退、重复或含旧版无序记录");
            previous = entry.SequenceNumber;
        }
        return Array.AsReadOnly(document.Entries);
    }

    public IReadOnlyList<CodexStateChangedEvent> EnsureCommitted(IReadOnlyList<CodexStateChangedEvent> history, CodexStateChangedEvent committed)
    {
        validateEvent(committed);
        if (committed.SchemaVersion != 2) throw new InvalidDataException("仅已提交的新版事件可写状态历史");
        if (history.Count > 0 && history[^1].SequenceNumber >= committed.SequenceNumber)
        {
            if (history[^1].SequenceNumber == committed.SequenceNumber && history[^1].EventId == committed.EventId) return history;
            throw new InvalidDataException("状态历史不能回退或重写相同序号");
        }
        if (history.Any(entry => entry.EventId == committed.EventId))
            throw new InvalidDataException("已提交事件身份不能重复进入状态历史");
        var updated = history.TakeLast(MaximumEntries - 1).Append(committed).ToArray();
        var document = new TimelineDocument { Entries = updated };
        if (JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }).LongLength > MaximumBytes)
            throw new InvalidDataException("有界状态历史超过大小上限，可靠最新记录仍保留");
        AtomicStore.Write(path, document);
        return Array.AsReadOnly(updated);
    }
}
