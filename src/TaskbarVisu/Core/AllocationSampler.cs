using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace TaskbarVisu.Core;

/// <summary>
/// Diagnostic (--dump) : échantillonne les allocations du programme par type, grâce aux événements
/// « AllocationTick » du runtime (un tous les ~100 Ko alloués). Sert à traquer les allocations par image.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();

    public static AllocationSampler? Instance { get; private set; }

    public static void Start() => Instance ??= new AllocationSampler();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // mot-clé GC
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.PayloadNames is null) return;
        int type = e.PayloadNames.IndexOf("TypeName"), amount = e.PayloadNames.IndexOf("AllocationAmount64");
        if (type < 0 || e.Payload?[type] is not string name) return;
        long bytes = amount >= 0 && e.Payload[amount] is ulong a ? (long)a : 100_000;
        _bytes.AddOrUpdate(name, bytes, (_, total) => total + bytes);
    }

    /// <summary>Types les plus alloués depuis le dernier relevé (puis remise à zéro).</summary>
    public IEnumerable<string> Report(int top = 12)
    {
        var snapshot = _bytes.ToArray();
        _bytes.Clear();
        return snapshot.OrderByDescending(p => p.Value).Take(top).Select(p => $"  {p.Value / 1024,8} Ko  {p.Key}");
    }
}
