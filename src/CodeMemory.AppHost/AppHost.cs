var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16");
    //.WithEnvironment("POSTGRES_USER", "codememory")
    //.WithEnvironment("POSTGRES_PASSWORD", "codememory")
    //.WithEnvironment("POSTGRES_DB", "codememory")
    //.WithLifetime(ContainerLifetime.Persistent);

var postgresDb = postgres.AddDatabase(name: "Npgsql", databaseName: "codememory");

var codememory = builder.AddProject<Projects.CodeMemory_AspNet>("codememory-aspnet")
    .WithReference(postgresDb)
    .WithEnvironment("ConnectionStrings__PgVector",
        postgresDb.Resource.ConnectionStringExpression)
    .WithEnvironment("Storage__Provider", "pgvector")
    .WithEnvironment("RepoRegistry__Provider", "postgresql")
    .WithEnvironment("RepoRegistry__CloneBasePath", Path.Combine(Directory.GetCurrentDirectory(), "cloned-repos"))
    //.WaitFor(postgres);
    .WaitFor(postgresDb);

//builder.AddProject<Projects.CodeMemory_AspNet>("codememory-aspnet");

builder.Build().Run();
