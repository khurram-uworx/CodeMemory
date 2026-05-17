using System.Numerics;
using System.Text.RegularExpressions;

namespace CodeMemory.Tests.Fixtures;

/// <summary>
/// A sample class for testing the parser pipeline.
/// </summary>
public class SampleClass
{
    public string Name { get; set; } = "default";

    public BigInteger BigNumber { get; } = BigInteger.Zero;

    public int Add(int a, int b)
    {
        return a + b;
    }

    public void DoNothing()
    {
        // intentional no-op
    }

    public Regex Pattern { get; } = new Regex(".*");

    public int FieldExample;

#pragma warning disable CS0067
    public event EventHandler? MyEvent;
#pragma warning restore CS0067
}

/// <summary>
/// A generic interface for testing.
/// </summary>
public interface IGenericRepository<T>
{
    T GetById(int id);
    void Save(T item);
}

/// <summary>
/// A struct for testing.
/// </summary>
public struct Point
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// An enum for testing.
/// </summary>
public enum Status
{
    Pending,
    Active,
    Completed
}

/// <summary>
/// A class with nested types for testing.
/// </summary>
public class OuterClass
{
    public class InnerClass
    {
        public void InnerMethod() { }
    }

    public enum InnerEnum
    {
        A,
        B
    }
}

/// <summary>
/// An abstract class for testing.
/// </summary>
public abstract class AbstractBase
{
    public abstract void AbstractMethod();
}

/// <summary>
/// A static class for testing.
/// </summary>
public static class UtilityClass
{
    public static string Format(string input) => input.ToUpper();
}

/// <summary>
/// A record class for testing.
/// </summary>
public record Person(string FirstName, string LastName);

/// <summary>
/// A class with generic method for testing.
/// </summary>
public class GenericMethodClass
{
    public T Echo<T>(T value) => value;

    public List<T> EchoList<T>(List<T> values) => values;
}
