using CodeMemory.Indexing;

namespace CodeMemory.Tests.Indexing;

[TestFixture]
public sealed class IndexingStateTests
{
    [Test]
    public void MarkDataUpdated_RaisesDataUpdatedEvent_WithCorrectRepoRoot()
    {
        var receivedArgs = new List<DataUpdatedEventArgs>();
        EventHandler<DataUpdatedEventArgs> handler = (_, args) => receivedArgs.Add(args);

        try
        {
            IndexingState.DataUpdated += handler;

            IndexingState.MarkDataUpdated("/test/repo1");

            Assert.That(receivedArgs.Count, Is.EqualTo(1));
            Assert.That(receivedArgs[0].RepoRoot, Is.EqualTo("/test/repo1"));
        }
        finally
        {
            IndexingState.DataUpdated -= handler;
        }
    }

    [Test]
    public void MarkDataUpdated_MultipleCalls_RaisesEventEachTime()
    {
        var receivedArgs = new List<DataUpdatedEventArgs>();
        EventHandler<DataUpdatedEventArgs> handler = (_, args) => receivedArgs.Add(args);

        try
        {
            IndexingState.DataUpdated += handler;

            IndexingState.MarkDataUpdated("/repo/A");
            IndexingState.MarkDataUpdated("/repo/B");
            IndexingState.MarkDataUpdated("/repo/C");

            Assert.That(receivedArgs.Count, Is.EqualTo(3));
            Assert.That(receivedArgs[0].RepoRoot, Is.EqualTo("/repo/A"));
            Assert.That(receivedArgs[1].RepoRoot, Is.EqualTo("/repo/B"));
            Assert.That(receivedArgs[2].RepoRoot, Is.EqualTo("/repo/C"));
        }
        finally
        {
            IndexingState.DataUpdated -= handler;
        }
    }

    [Test]
    public void MarkDataUpdated_AfterUnsubscribe_DoesNotCallHandler()
    {
        var receivedArgs = new List<DataUpdatedEventArgs>();
        EventHandler<DataUpdatedEventArgs> handler = (_, args) => receivedArgs.Add(args);

        IndexingState.DataUpdated += handler;
        IndexingState.MarkDataUpdated("/repo/before");
        Assert.That(receivedArgs.Count, Is.EqualTo(1));

        IndexingState.DataUpdated -= handler;
        IndexingState.MarkDataUpdated("/repo/after");
        Assert.That(receivedArgs.Count, Is.EqualTo(1));
    }

    [Test]
    public void MarkDataUpdated_MultipleSubscribers_AllCalled()
    {
        var received1 = new List<DataUpdatedEventArgs>();
        var received2 = new List<DataUpdatedEventArgs>();
        var received3 = new List<DataUpdatedEventArgs>();

        EventHandler<DataUpdatedEventArgs> handler1 = (_, args) => received1.Add(args);
        EventHandler<DataUpdatedEventArgs> handler2 = (_, args) => received2.Add(args);
        EventHandler<DataUpdatedEventArgs> handler3 = (_, args) => received3.Add(args);

        try
        {
            IndexingState.DataUpdated += handler1;
            IndexingState.DataUpdated += handler2;
            IndexingState.DataUpdated += handler3;

            IndexingState.MarkDataUpdated("/test/multi");

            Assert.That(received1.Count, Is.EqualTo(1));
            Assert.That(received2.Count, Is.EqualTo(1));
            Assert.That(received3.Count, Is.EqualTo(1));

            Assert.That(received1[0].RepoRoot, Is.EqualTo("/test/multi"));
            Assert.That(received2[0].RepoRoot, Is.EqualTo("/test/multi"));
            Assert.That(received3[0].RepoRoot, Is.EqualTo("/test/multi"));
        }
        finally
        {
            IndexingState.DataUpdated -= handler1;
            IndexingState.DataUpdated -= handler2;
            IndexingState.DataUpdated -= handler3;
        }
    }

    [Test]
    public void MarkDataUpdated_NullSubscribers_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => IndexingState.MarkDataUpdated("/test/no-subscribers"));
    }

    [Test]
    public void DataUpdatedEventArgs_Constructor_SetsRepoRoot()
    {
        var args = new DataUpdatedEventArgs("/my/repo/path");
        Assert.That(args.RepoRoot, Is.EqualTo("/my/repo/path"));
    }

    [Test]
    public void IsCompleted_WithoutRepos_ReturnsFalse()
    {
        Assert.That(IndexingState.IsCompleted(), Is.False);
    }

    [Test]
    public void MarkCompleted_SetsRepoToCompleted()
    {
        const string repoName = "test-repo-completion";

        Assert.That(IndexingState.IsCompleted(repoName), Is.False);

        IndexingState.MarkCompleted(repoName);

        Assert.That(IndexingState.IsCompleted(repoName), Is.True);

        IndexingState.MarkIncomplete(repoName);

        Assert.That(IndexingState.IsCompleted(repoName), Is.False);
    }

    [Test]
    public void UpdateProgress_TracksProgressCorrectly()
    {
        const string repoName = "test-repo-progress";

        Assert.That(IndexingState.GetProgress(repoName), Is.Null);

        IndexingState.UpdateProgress(repoName, 0.25);
        Assert.That(IndexingState.GetProgress(repoName), Is.EqualTo(0.25));

        IndexingState.UpdateProgress(repoName, 0.5);
        Assert.That(IndexingState.GetProgress(repoName), Is.EqualTo(0.5));

        IndexingState.UpdateProgress(repoName, 1.0);
        Assert.That(IndexingState.GetProgress(repoName), Is.EqualTo(1.0));

        var allProgress = IndexingState.GetAllProgress();
        Assert.That(allProgress.ContainsKey(repoName), Is.True);
        Assert.That(allProgress[repoName], Is.EqualTo(1.0));

        IndexingState.ClearProgress(repoName);
        Assert.That(IndexingState.GetProgress(repoName), Is.Null);
    }

    [Test]
    public void MarkFileWatcherActive_SetsFlag()
    {
        Assert.That(IndexingState.IsFileWatcherActive, Is.False);

        IndexingState.MarkFileWatcherActive();

        Assert.That(IndexingState.IsFileWatcherActive, Is.True);
    }
}
