using CodeMemory.Storage;

namespace CodeMemory.AspNet.Storage;

public delegate IStorageService StorageFactory(string repoName, string repoPath, int repoId);
