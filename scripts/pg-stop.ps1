$container = "codememory-pgvector"

if (docker ps -a --filter "name=$container" --format "{{.Names}}" | Select-String $container) {
    docker stop $container
    docker rm $container
    Write-Host "Stopped and removed"
} else {
    Write-Host "Not running"
}
