using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage;

public static class VectorSchema
{
    public static VectorStoreCollectionDefinition CreateChunkDefinition(int dimension)
    {
        return new()
        {
            Properties =
            [
                new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreDataProperty("SymbolId", typeof(string)),
                new VectorStoreDataProperty("FilePath", typeof(string)),
                new VectorStoreDataProperty("Content", typeof(string)),
                new VectorStoreDataProperty("Language", typeof(string)),
                new VectorStoreDataProperty("LineStart", typeof(int)),
                new VectorStoreDataProperty("LineEnd", typeof(int)),
                new VectorStoreDataProperty("MetadataJson", typeof(string)) { StorageName = "metadata" },
                new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), dimension)
                {
                    DistanceFunction = DistanceFunction.CosineDistance
                }
            ]
        };
    }
}
