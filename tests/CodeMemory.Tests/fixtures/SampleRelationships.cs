namespace CodeMemory.Tests.Fixtures;

public interface IMyInterface
{
    void DoSomething();
}

public class MyBaseClass
{
    public void BaseMethod() { }
}

public class MyDerivedClass : MyBaseClass, IMyInterface
{
    public void DoSomething()
    {
        var obj = new MyBaseClass();
        obj.BaseMethod();
    }
}

public class ReferenceHolder
{
    public MyBaseClass? Reference { get; set; }

    public void Process(MyBaseClass input)
    {
        var local = new MyDerivedClass();
        local.DoSomething();
    }
}
