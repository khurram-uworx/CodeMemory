namespace CodeMemory.Services.Architecture;

public interface IComponentResolver
{
    string GetComponentName(string filePath, int depth = 1);
}
