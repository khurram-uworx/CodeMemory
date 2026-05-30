using CodeMemory.AspNet.Models;

namespace CodeMemory.AspNet.Storage;

public interface IMetricsStore
{
    void RecordCounter(
        string instrumentName,
        long value,
        IReadOnlyList<KeyValuePair<string, object?>>? tags);

    void RecordHistogram(
        string instrumentName,
        double value,
        IReadOnlyList<KeyValuePair<string, object?>>? tags);

    RuntimeMetricsSnapshot GetSnapshot(bool reset = false);
}
