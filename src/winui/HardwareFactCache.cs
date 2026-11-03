using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class HardwareFactCache
{
    private sealed class Group(string id)
    {
        internal readonly string Id = id;
        internal readonly List<KeyValuePair<string, string>> Facts = [];
        internal Dictionary<string, string> Values = [];
        internal int Count;
        internal bool Changed;

        internal void Append(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            int index = Count++;
            if (index == Facts.Count) {
                Facts.Add(new(name.ToString(), value.ToString()));
                Changed = true;
                return;
            }
            var before = Facts[index];
            bool sameName = name.SequenceEqual(before.Key);
            bool sameValue = value.SequenceEqual(before.Value);
            if (sameName && sameValue) return;
            Facts[index] = new(sameName ? before.Key : name.ToString(), sameValue ? before.Value : value.ToString());
            Changed = true;
        }

        internal void Complete()
        {
            if (Count < Facts.Count) {
                Facts.RemoveRange(Count, Facts.Count - Count);
                Changed = true;
            }
            if (!Changed) return;
            var values = new Dictionary<string, string>(Facts.Count, StringComparer.Ordinal);
            foreach (var fact in Facts) values[fact.Key] = fact.Value;
            Values = values;
        }
    }

    private readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
    private readonly List<string> removed = [];
    internal Dictionary<string, Dictionary<string, string>> Values { get; } = new(StringComparer.Ordinal);

    internal unsafe void Apply(HardwareData data, FrameFlags scope)
    {
        if ((scope & (FrameFlags.ProcessData | FrameFlags.ResourceData)) == 0) return;
        foreach (var group in groups.Values)
            if (Includes(group.Id, scope)) { group.Count = 0; group.Changed = false; }
        var lookup = groups.GetAlternateLookup<ReadOnlySpan<char>>();
        for (uint i = 0; i < data.FactCount; ++i) {
            var fact = data.Facts[i];
            var id = Span(fact.ResourceId);
            if (!lookup.TryGetValue(id, out var group)) {
                group = new Group(id.ToString());
                groups.Add(group.Id, group);
            }
            group.Append(Span(fact.Name), Span(fact.Value));
        }
        removed.Clear();
        foreach (var group in groups.Values) {
            if (!Includes(group.Id, scope)) continue;
            if (group.Count == 0) { removed.Add(group.Id); continue; }
            group.Complete();
            Values[group.Id] = group.Values;
        }
        foreach (string id in removed) { groups.Remove(id); Values.Remove(id); }
    }

    private static bool Includes(string id, FrameFlags scope) => id is "cpu" or "memory" or "board"
        ? (scope & FrameFlags.ProcessData) != 0 : (scope & FrameFlags.ResourceData) != 0;

    private static unsafe ReadOnlySpan<char> Span(String16 value) => value.Length == 0
        ? ReadOnlySpan<char>.Empty : new(value.Data, checked((int)value.Length));
}
