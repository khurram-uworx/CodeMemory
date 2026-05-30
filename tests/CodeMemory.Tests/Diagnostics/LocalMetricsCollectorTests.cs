using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Services;
using CodeMemory.AspNet.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

namespace CodeMemory.Tests.Diagnostics;

public sealed class InMemoryMetricsStoreTests
{
    static readonly LocalMetricsOptions DefaultOptions = new() { Enabled = true };
    static InMemoryMetricsStore CreateStore(LocalMetricsOptions? options = null)
        => new(Options.Create(options ?? DefaultOptions), NullLogger<InMemoryMetricsStore>.Instance);

    [Test]
    public void GetSnapshot_Disabled_ReturnsEmpty()
    {
        var store = CreateStore(new LocalMetricsOptions { Enabled = false });
        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Is.Empty);
    }

    [Test]
    public void RecordCounter_SingleIncrement_ReflectedInSnapshot()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 5, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(1));
        Assert.That(snapshot.Instruments[0].Name, Is.EqualTo("test.counter"));
        Assert.That(snapshot.Instruments[0].InstrumentType, Is.EqualTo("counter"));
        Assert.That(snapshot.Instruments[0].Values, Has.Count.EqualTo(1));
        Assert.That(snapshot.Instruments[0].Values[0].Count, Is.EqualTo(5));
    }

    [Test]
    public void RecordCounter_MultipleIncrements_Accumulates()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 1, null);
        store.RecordCounter("test.counter", 2, null);
        store.RecordCounter("test.counter", 3, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments[0].Values[0].Count, Is.EqualTo(6));
    }

    [Test]
    public void RecordHistogram_Values_ReflectedInSnapshot()
    {
        var store = CreateStore();
        store.RecordHistogram("test.histogram", 10.0, null);
        store.RecordHistogram("test.histogram", 20.0, null);
        store.RecordHistogram("test.histogram", 30.0, null);

        var snapshot = store.GetSnapshot();
        var values = snapshot.Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(3));
        Assert.That(values.Sum, Is.EqualTo(60.0));
        Assert.Multiple(() =>
        {
            Assert.That(values.Min, Is.EqualTo(10.0));
            Assert.That(values.Max, Is.EqualTo(30.0));
            Assert.That(values.Last, Is.EqualTo(30.0));
        });
    }

    [Test]
    public void RecordHistogram_SingleValue_AllStatsMatch()
    {
        var store = CreateStore();
        store.RecordHistogram("test.histogram", 42.5, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(values.Min, Is.EqualTo(42.5));
            Assert.That(values.Max, Is.EqualTo(42.5));
            Assert.That(values.Last, Is.EqualTo(42.5));
            Assert.That(values.Sum, Is.EqualTo(42.5));
        });
    }

    [Test]
    public void TaggedInstruments_TrackedSeparately()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 10,
            [new KeyValuePair<string, object?>("tool", "ping")]);
        store.RecordCounter("test.counter", 20,
            [new KeyValuePair<string, object?>("tool", "search")]);

        var snapshot = store.GetSnapshot();
        var values = snapshot.Instruments[0].Values;
        Assert.That(values, Has.Count.EqualTo(2));

        var pingValue = values.First(v => v.Tags?.Any(t => t.Value == "ping") == true);
        Assert.That(pingValue.Count, Is.EqualTo(10));

        var searchValue = values.First(v => v.Tags?.Any(t => t.Value == "search") == true);
        Assert.That(searchValue.Count, Is.EqualTo(20));
    }

    [Test]
    public void TaglessInstruments_HaveNullTags()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 5, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Tags, Is.Null);
    }

    [Test]
    public void MultipleTags_SerializedAndDeserialized()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 7,
            [
                new KeyValuePair<string, object?>("host", "aspnet"),
                new KeyValuePair<string, object?>("tool", "ping")
            ]);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Tags, Has.Count.EqualTo(2));
        Assert.That(values.Tags, Does.Contain(new KeyValuePair<string, string>("host", "aspnet")));
        Assert.That(values.Tags, Does.Contain(new KeyValuePair<string, string>("tool", "ping")));
    }

    [Test]
    public void MaxUniqueTagCombinations_DropsExcess()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxUniqueTagCombinations = 2 };
        var store = CreateStore(options);

        // Register two tag combos
        store.RecordCounter("test.counter", 1, [new("a", "1")]);
        store.RecordCounter("test.counter", 1, [new("a", "2")]);

        // Third tag combo should be dropped
        store.RecordCounter("test.counter", 1, [new("a", "3")]);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments[0].Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void MaxMeasurementsPerTagSet_RingBuffer()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 3 };
        var store = CreateStore(options);

        store.RecordHistogram("test.histogram", 1.0, null);
        store.RecordHistogram("test.histogram", 2.0, null);
        store.RecordHistogram("test.histogram", 3.0, null);
        store.RecordHistogram("test.histogram", 4.0, null);
        store.RecordHistogram("test.histogram", 5.0, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Measurements, Has.Count.EqualTo(3));
        Assert.That(values.Measurements[0].Value, Is.EqualTo(3.0));
        Assert.That(values.Measurements[1].Value, Is.EqualTo(4.0));
        Assert.That(values.Measurements[2].Value, Is.EqualTo(5.0));
    }

    [Test]
    public void MaxMeasurementsPerTagSet_Zero_NoMeasurements()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 0 };
        var store = CreateStore(options);

        store.RecordHistogram("test.histogram", 1.0, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Measurements, Is.Null);
        Assert.That(values.Count, Is.EqualTo(1));
    }

    [Test]
    public void Counter_RingBuffer_TracksPerCallValues()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 3 };
        var store = CreateStore(options);

        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 1250, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(3250));
        Assert.That(values.Measurements, Has.Count.EqualTo(3));
        Assert.That(values.Measurements[0].Value, Is.EqualTo(1000));
        Assert.That(values.Measurements[1].Value, Is.EqualTo(1000));
        Assert.That(values.Measurements[2].Value, Is.EqualTo(1250));
    }

    [Test]
    public void Counter_RingBuffer_RespectsMaxDepth()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 2 };
        var store = CreateStore(options);

        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 1250, null);
        store.RecordCounter("test.counter", 1500, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(3750));
        Assert.That(values.Measurements, Has.Count.EqualTo(2));
        Assert.That(values.Measurements[0].Value, Is.EqualTo(1250));
        Assert.That(values.Measurements[1].Value, Is.EqualTo(1500));
    }

    [Test]
    public void Counter_RingBuffer_WithReset()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 3 };
        var store = CreateStore(options);

        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 1250, null);

        var snap1 = store.GetSnapshot(reset: true);
        Assert.That(snap1.Instruments[0].Values[0].Count, Is.EqualTo(2250));
        Assert.That(snap1.Instruments[0].Values[0].Measurements, Has.Count.EqualTo(2));

        var snap2 = store.GetSnapshot();
        Assert.That(snap2.Instruments[0].Values[0].Count, Is.EqualTo(0));
        Assert.That(snap2.Instruments[0].Values[0].Measurements, Is.Null);
    }

    [Test]
    public void Counter_NoRingBuffer_NoMeasurements()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 0 };
        var store = CreateStore(options);

        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 2000, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(3000));
        Assert.That(values.Measurements, Is.Null);
    }

    [Test]
    public void GetSnapshot_Reset_ResetsValues()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 10, null);
        store.RecordHistogram("test.histogram", 5.0, null);

        // Read and reset
        var snapshot1 = store.GetSnapshot(reset: true);
        var counter1 = snapshot1.Instruments.First(i => i.Name == "test.counter");
        Assert.That(counter1.Values[0].Count, Is.EqualTo(10));

        // Read again — should be zero
        var snapshot2 = store.GetSnapshot();
        var counter2 = snapshot2.Instruments.First(i => i.Name == "test.counter");
        Assert.That(counter2.Values[0].Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshot_WithoutReset_PreservesValues()
    {
        var store = CreateStore();
        store.RecordCounter("test.counter", 10, null);

        var snapshot1 = store.GetSnapshot(reset: false);
        var snapshot2 = store.GetSnapshot(reset: false);

        Assert.That(snapshot1.Instruments[0].Values[0].Count, Is.EqualTo(10));
        Assert.That(snapshot2.Instruments[0].Values[0].Count, Is.EqualTo(10));
    }

    [Test]
    public void MultipleInstruments_AllCaptured()
    {
        var store = CreateStore();
        store.RecordCounter("counter.a", 1, null);
        store.RecordHistogram("histogram.b", 2.0, null);
        store.RecordCounter("counter.c", 3, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(3));
        Assert.That(snapshot.Instruments.Select(i => i.Name),
            Is.EquivalentTo(new[] { "counter.a", "histogram.b", "counter.c" }));
    }

    [Test]
    public void ConcurrentAccess_ConsistentSnapshot()
    {
        var store = CreateStore();
        var threads = new List<Thread>();

        for (int i = 0; i < 4; i++)
        {
            threads.Add(new Thread(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    store.RecordCounter("concurrent.counter", 1, null);
                    store.RecordHistogram("concurrent.histogram", j, null);
                }
            }));
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(2));

        var counterValue = snapshot.Instruments.First(i => i.Name == "concurrent.counter");
        Assert.That(counterValue.Values[0].Count, Is.EqualTo(400));

        var histValue = snapshot.Instruments.First(i => i.Name == "concurrent.histogram");
        Assert.That(histValue.Values[0].Count, Is.EqualTo(400));
    }
}

public sealed class LocalMetricsCollectorPipelineTests
{
    [Test]
    public void Collector_Enabled_RecordsInstruments()
    {
        var options = Options.Create(new LocalMetricsOptions { Enabled = true });
        var store = new InMemoryMetricsStore(options, NullLogger<InMemoryMetricsStore>.Instance);
        var collector = new LocalMetricsCollector(store, options);

        // Emit through a test instrument on the CodeMemory meter
        var meter = new Meter("CodeMemory", "1.0");
        var counter = meter.CreateCounter<long>("test.pipeline.counter");
        counter.Add(42);

        // Give the MeterListener time to deliver the measurement
        var emitted = SpinWait.SpinUntil(() =>
        {
            var snap = collector.GetSnapshot();
            return snap?.Instruments.Any(i => i.Name == "test.pipeline.counter") == true;
        }, TimeSpan.FromSeconds(2));

        Assert.That(emitted, Is.True, "Measurement was not delivered within timeout");

        var snapshot = collector.GetSnapshot()!;
        var inst = snapshot.Instruments.First(i => i.Name == "test.pipeline.counter");
        Assert.That(inst.Values[0].Count, Is.EqualTo(42));

        collector.Dispose();
        meter.Dispose();
    }

    [Test]
    public void Collector_Disabled_ReturnsNull()
    {
        var options = Options.Create(new LocalMetricsOptions { Enabled = false });
        var store = new InMemoryMetricsStore(options, NullLogger<InMemoryMetricsStore>.Instance);
        var collector = new LocalMetricsCollector(store, options);

        Assert.That(collector.IsEnabled, Is.False);
        Assert.That(collector.GetSnapshot(), Is.Null);

        collector.Dispose();
    }

    [Test]
    public void Collector_Disposed_StopsListening()
    {
        var options = Options.Create(new LocalMetricsOptions { Enabled = true });
        var store = new InMemoryMetricsStore(options, NullLogger<InMemoryMetricsStore>.Instance);
        var collector = new LocalMetricsCollector(store, options);

        collector.Dispose();

        var meter = new Meter("CodeMemory", "1.0");
        var counter = meter.CreateCounter<long>("test.disposed.counter");
        counter.Add(99);

        // Small wait to confirm no delivery
        Thread.Sleep(500);

        var snapshot = collector.GetSnapshot()!;
        Assert.That(snapshot.Instruments.Any(i => i.Name == "test.disposed.counter"), Is.False);

        meter.Dispose();
    }

    [Test]
    public void Collector_RecordsHistogramViaDouble()
    {
        var options = Options.Create(new LocalMetricsOptions { Enabled = true });
        var store = new InMemoryMetricsStore(options, NullLogger<InMemoryMetricsStore>.Instance);
        var collector = new LocalMetricsCollector(store, options);

        var meter = new Meter("CodeMemory", "1.0");
        var hist = meter.CreateHistogram<double>("test.pipeline.histogram");
        hist.Record(15.5);

        var emitted = SpinWait.SpinUntil(() =>
        {
            var snap = collector.GetSnapshot();
            return snap?.Instruments.Any(i => i.Name == "test.pipeline.histogram") == true;
        }, TimeSpan.FromSeconds(2));

        Assert.That(emitted, Is.True, "Histogram measurement not delivered");

        var snapshot = collector.GetSnapshot()!;
        var inst = snapshot.Instruments.First(i => i.Name == "test.pipeline.histogram");
        Assert.That(inst.Values[0].Count, Is.EqualTo(1));
        Assert.That(inst.Values[0].Last, Is.EqualTo(15.5));

        collector.Dispose();
        meter.Dispose();
    }
}
