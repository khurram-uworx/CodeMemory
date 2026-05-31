using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeMemory.Tests.Diagnostics;

public sealed class SqliteMetricsStoreTests
{
    static readonly LocalMetricsOptions DefaultOptions = new() { Enabled = true };

    static SqliteMetricsStore createStore(LocalMetricsOptions? options = null)
        => new("Data Source=:memory:",
               options ?? DefaultOptions,
               NullLogger<SqliteMetricsStore>.Instance);

    [Test]
    public void GetSnapshot_Empty_ReturnsEmpty()
    {
        var store = createStore();
        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Is.Empty);
    }

    [Test]
    public void RecordCounter_SingleIncrement_ReflectedInSnapshot()
    {
        var store = createStore();
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
        var store = createStore();
        store.RecordCounter("test.counter", 1, null);
        store.RecordCounter("test.counter", 2, null);
        store.RecordCounter("test.counter", 3, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments[0].Values[0].Count, Is.EqualTo(6));
    }

    [Test]
    public void RecordHistogram_Values_ReflectedInSnapshot()
    {
        var store = createStore();
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
        var store = createStore();
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
        var store = createStore();
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
        var store = createStore();
        store.RecordCounter("test.counter", 5, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Tags, Is.Null);
    }

    [Test]
    public void MultipleTags_SerializedAndDeserialized()
    {
        var store = createStore();
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
        var store = createStore(options);

        store.RecordCounter("test.counter", 1, [new("a", "1")]);
        store.RecordCounter("test.counter", 1, [new("a", "2")]);

        store.RecordCounter("test.counter", 1, [new("a", "3")]);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments[0].Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void MaxMeasurementsPerTagSet_RingBuffer()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 3 };
        var store = createStore(options);

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
        var store = createStore(options);

        store.RecordHistogram("test.histogram", 1.0, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Measurements, Is.Null);
        Assert.That(values.Count, Is.EqualTo(1));
    }

    [Test]
    public void Counter_RingBuffer_TracksPerCallValues()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 3 };
        var store = createStore(options);

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
        var store = createStore(options);

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
        var store = createStore(options);

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
        var store = createStore(options);

        store.RecordCounter("test.counter", 1000, null);
        store.RecordCounter("test.counter", 2000, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Count, Is.EqualTo(3000));
        Assert.That(values.Measurements, Is.Null);
    }

    [Test]
    public void GetSnapshot_Reset_ResetsValues()
    {
        var store = createStore();
        store.RecordCounter("test.counter", 10, null);
        store.RecordHistogram("test.histogram", 5.0, null);

        var snapshot1 = store.GetSnapshot(reset: true);
        var counter1 = snapshot1.Instruments.First(i => i.Name == "test.counter");
        Assert.That(counter1.Values[0].Count, Is.EqualTo(10));

        var snapshot2 = store.GetSnapshot();
        var counter2 = snapshot2.Instruments.First(i => i.Name == "test.counter");
        Assert.That(counter2.Values[0].Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshot_WithoutReset_PreservesValues()
    {
        var store = createStore();
        store.RecordCounter("test.counter", 10, null);

        var snapshot1 = store.GetSnapshot(reset: false);
        var snapshot2 = store.GetSnapshot(reset: false);

        Assert.That(snapshot1.Instruments[0].Values[0].Count, Is.EqualTo(10));
        Assert.That(snapshot2.Instruments[0].Values[0].Count, Is.EqualTo(10));
    }

    [Test]
    public void MultipleInstruments_AllCaptured()
    {
        var store = createStore();
        store.RecordCounter("counter.a", 1, null);
        store.RecordHistogram("histogram.b", 2.0, null);
        store.RecordCounter("counter.c", 3, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(3));
        Assert.That(snapshot.Instruments.Select(i => i.Name),
            Is.EquivalentTo(new[] { "counter.a", "histogram.b", "counter.c" }));
    }

    [Test]
    public void RecordGauge_SingleUpdate_ReflectedInSnapshot()
    {
        var store = createStore();
        store.RecordGauge("test.gauge", 42, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(1));
        Assert.That(snapshot.Instruments[0].Name, Is.EqualTo("test.gauge"));
        Assert.That(snapshot.Instruments[0].InstrumentType, Is.EqualTo("gauge"));
        Assert.That(snapshot.Instruments[0].Values, Has.Count.EqualTo(1));
        Assert.That(snapshot.Instruments[0].Values[0].Last, Is.EqualTo(42));
        Assert.That(snapshot.Instruments[0].Values[0].Count, Is.Null);
        Assert.That(snapshot.Instruments[0].Values[0].Sum, Is.Null);
        Assert.That(snapshot.Instruments[0].Values[0].Min, Is.Null);
    }

    [Test]
    public void RecordGauge_MultipleUpdates_LastValuePreserved()
    {
        var store = createStore();
        store.RecordGauge("test.gauge", 10, null);
        store.RecordGauge("test.gauge", 20, null);
        store.RecordGauge("test.gauge", 30, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Last, Is.EqualTo(30));
    }

    [Test]
    public void RecordGauge_WithRingBuffer_CapturesMeasurements()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 10 };
        var store = createStore(options);

        store.RecordGauge("test.gauge", 100, null);
        store.RecordGauge("test.gauge", 200, null);
        store.RecordGauge("test.gauge", 300, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Last, Is.EqualTo(300));
        Assert.That(values.Measurements, Is.Not.Null);
        Assert.That(values.Measurements, Has.Count.EqualTo(3));
        Assert.That(values.Measurements[0].Value, Is.EqualTo(100));
        Assert.That(values.Measurements[2].Value, Is.EqualTo(300));
    }

    [Test]
    public void RecordGauge_WithRingBuffer_TrimsToCapacity()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 2 };
        var store = createStore(options);

        store.RecordGauge("test.gauge", 1, null);
        store.RecordGauge("test.gauge", 2, null);
        store.RecordGauge("test.gauge", 3, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Measurements, Has.Count.EqualTo(2));
        Assert.That(values.Measurements[0].Value, Is.EqualTo(2));
        Assert.That(values.Measurements[1].Value, Is.EqualTo(3));
    }

    [Test]
    public void RecordGauge_WithoutRingBuffer_NoMeasurements()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 0 };
        var store = createStore(options);

        store.RecordGauge("test.gauge", 100, null);
        store.RecordGauge("test.gauge", 200, null);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Last, Is.EqualTo(200));
        Assert.That(values.Measurements, Is.Null);
    }

    [Test]
    public void RecordGauge_WithTags_Captured()
    {
        var store = createStore();
        store.RecordGauge("test.gauge", 42, [new("repo", "myrepo")]);

        var values = store.GetSnapshot().Instruments[0].Values[0];
        Assert.That(values.Last, Is.EqualTo(42));
        Assert.That(values.Tags, Is.Not.Null);
        Assert.That(values.Tags[0], Is.EqualTo(new KeyValuePair<string, string>("repo", "myrepo")));
    }

    [Test]
    public void RecordGauge_WithReset_ClearsLastAndMeasurements()
    {
        var options = new LocalMetricsOptions { Enabled = true, MaxMeasurementsPerTagSet = 10 };
        var store = createStore(options);

        store.RecordGauge("test.gauge", 100, null);
        store.RecordGauge("test.gauge", 200, null);

        var snapshot1 = store.GetSnapshot(reset: true);
        var gauge1 = snapshot1.Instruments.First(i => i.Name == "test.gauge");
        Assert.That(gauge1.Values[0].Last, Is.EqualTo(200));
        Assert.That(gauge1.Values[0].Measurements, Has.Count.EqualTo(2));

        var snapshot2 = store.GetSnapshot();
        var gauge2 = snapshot2.Instruments.First(i => i.Name == "test.gauge");
        Assert.That(gauge2.Values[0].Last, Is.EqualTo(0));
        Assert.That(gauge2.Values[0].Measurements, Is.Null);
    }

    [Test]
    public void RecordGauge_WithoutReset_PreservesValues()
    {
        var store = createStore();
        store.RecordGauge("test.gauge", 42, null);

        var snapshot1 = store.GetSnapshot(reset: false);
        var snapshot2 = store.GetSnapshot(reset: false);

        Assert.That(snapshot1.Instruments[0].Values[0].Last, Is.EqualTo(42));
        Assert.That(snapshot2.Instruments[0].Values[0].Last, Is.EqualTo(42));
    }

    [Test]
    public void RemoveInstrumentTags_RemovesTagSet()
    {
        var store = createStore();
        store.RecordCounter("test.counter", 10, [new("a", "1")]);
        store.RecordCounter("test.counter", 20, [new("a", "2")]);

        var before = store.GetSnapshot();
        Assert.That(before.Instruments[0].Values, Has.Count.EqualTo(2));

        store.RemoveInstrumentTags("test.counter", [new("a", "1")]);

        var after = store.GetSnapshot();
        Assert.That(after.Instruments[0].Values, Has.Count.EqualTo(1));
        Assert.That(after.Instruments[0].Values[0].Count, Is.EqualTo(20));
    }

    [Test]
    public void Dispose_CleansUpConnection()
    {
        var store = createStore();
        store.RecordCounter("test.counter", 5, null);
        Assert.DoesNotThrow(() => store.Dispose());
    }

    [Test]
    public void SameName_DifferentTypes_SharedInstrumentCounterWins()
    {
        var store = createStore();
        store.RecordCounter("test.mixed", 5, null);
        store.RecordHistogram("test.mixed", 10.0, null);

        var snapshot = store.GetSnapshot();
        Assert.That(snapshot.Instruments, Has.Count.EqualTo(1));

        var inst = snapshot.Instruments[0];
        Assert.That(inst.InstrumentType, Is.EqualTo("counter"));
        Assert.That(inst.Values[0].Count, Is.EqualTo(5));
    }
}
