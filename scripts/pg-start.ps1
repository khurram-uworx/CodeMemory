$container = "codememory-pgvector"

if (docker ps --filter "name=$container" --format "{{.Names}}" | Select-String $container) {
    Write-Host "Already running"
    exit
}

if (docker ps -a --filter "name=$container" --format "{{.Names}}" | Select-String $container) {
    docker start $container
} else {
    docker run -d --name $container -e POSTGRES_USER=codememory -e POSTGRES_PASSWORD=codememory -e POSTGRES_DB=codememory -p 5432:5432 pgvector/pgvector:pg17
}

Write-Host "Ready: Host=localhost;Port=5432;Database=codememory;Username=codememory;Password=codememory"
