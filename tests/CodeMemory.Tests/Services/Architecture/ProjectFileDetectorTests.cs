using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Architecture;

public sealed class ProjectFileDetectorTests
{
    static ProjectFileDetector CreateDetector()
        => new(NullLogger<ProjectFileDetector>.Instance);

    static string CreateTempRepo(params (string RelativeDir, string FileName)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryDetectorTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        foreach (var (relativeDir, fileName) in files)
        {
            var fullDir = string.IsNullOrEmpty(relativeDir) ? dir : Path.Combine(dir, relativeDir);
            Directory.CreateDirectory(fullDir);
            File.WriteAllText(Path.Combine(fullDir, fileName), "");
        }
        return dir;
    }

    [Test]
    public void Discover_RepoRootDoesNotExist_ReturnsEmpty()
    {
        var detector = CreateDetector();
        var result = detector.Discover("Z:\\nonexistent\\path\\detector_test_" + Guid.NewGuid().ToString("N"));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Discover_NoBuildFiles_ReturnsEmpty()
    {
        var repoDir = CreateTempRepo(("", "readme.md"), ("src", "file.cs"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Is.Empty);
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_SingleCsproj_ReturnsOneMsBuildComponent()
    {
        var repoDir = CreateTempRepo(("", "readme.md"), ("src/MyApp", "MyApp.csproj"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].ComponentName, Is.EqualTo("MyApp"));
            Assert.That(result[0].ComponentKind, Is.EqualTo(ComponentKind.MsBuild));
            Assert.That(result[0].ComponentType, Is.EqualTo(ComponentType.Component));
            Assert.That(result[0].BuildFileDirectory, Is.EqualTo("src/MyApp"));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_ComponentInTestDirectory_SetsTestType()
    {
        var repoDir = CreateTempRepo(("tests/MyApp.Tests", "MyApp.Tests.csproj"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].ComponentType, Is.EqualTo(ComponentType.Test));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_MultipleBuildFilesInSameDir_DeduplicatesToOneComponent()
    {
        var repoDir = CreateTempRepo(
            ("src/MyApp", "MyApp.csproj"),
            ("src/MyApp", "MyApp.csproj"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(1));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_MultipleComponents_ReturnsAll()
    {
        var repoDir = CreateTempRepo(
            ("src/App", "App.csproj"),
            ("src/Lib", "Lib.csproj"),
            ("tests/App.Tests", "App.Tests.csproj"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result.Any(c => c.ComponentName == "App"), Is.True);
            Assert.That(result.Any(c => c.ComponentName == "Lib"), Is.True);
            Assert.That(result.Any(c => c.ComponentName == "App.Tests"), Is.True);
            Assert.That(result.First(c => c.ComponentName == "App.Tests").ComponentType, Is.EqualTo(ComponentType.Test));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_KnownBuildFileTypes_DetectsCorrectKind()
    {
        var repoDir = CreateTempRepo(
            ("frontend", "package.json"),
            ("backend", "pom.xml"),
            ("mobile", "build.gradle"),
            ("python", "pyproject.toml"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.First(c => c.ComponentName == "frontend").ComponentKind, Is.EqualTo(ComponentKind.Node));
            Assert.That(result.First(c => c.ComponentName == "backend").ComponentKind, Is.EqualTo(ComponentKind.Maven));
            Assert.That(result.First(c => c.ComponentName == "mobile").ComponentKind, Is.EqualTo(ComponentKind.Gradle));
            Assert.That(result.First(c => c.ComponentName == "python").ComponentKind, Is.EqualTo(ComponentKind.Python));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_FileCount_CountsAllFilesInComponentDir()
    {
        var repoDir = CreateTempRepo(
            ("src/MyApp", "MyApp.csproj"),
            ("src/MyApp", "Class1.cs"),
            ("src/MyApp", "Class2.cs"),
            ("src/MyApp/SubDir", "Helper.cs"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].FileCount, Is.EqualTo(4)); // csproj + 2 cs + Helper.cs = 4
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_WithPreCollectedFiles_ReturnsComponentsFromList()
    {
        var repoDir = CreateTempRepo(
            ("src/MyApp", "MyApp.csproj"),
            ("src/MyApp", "Class1.cs"),
            ("docs", "readme.md"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir, [Path.Combine(repoDir, "src/MyApp/MyApp.csproj")]);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].ComponentName, Is.EqualTo("MyApp"));
            Assert.That(result[0].ComponentKind, Is.EqualTo(ComponentKind.MsBuild));
            Assert.That(result[0].BuildFileDirectory, Is.EqualTo("src/MyApp"));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public void Discover_WithPreCollectedFiles_UnknownBuildFileDefaultsToMsBuild()
    {
        var repoDir = CreateTempRepo(("src/Tool", "tool.sln"));
        try
        {
            var detector = CreateDetector();
            var result = detector.Discover(repoDir, [Path.Combine(repoDir, "src/Tool/tool.sln")]);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].ComponentKind, Is.EqualTo(ComponentKind.MsBuild));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }
}
