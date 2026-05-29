FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CodeMemory.AspNet/CodeMemory.AspNet.csproj
RUN dotnet publish src/CodeMemory.AspNet/CodeMemory.AspNet.csproj -c Release -r linux-x64 --no-self-contained -o /app

# Build extensions assembly (loaded at runtime by reflection, only when Embedding:Provider=onnx)
RUN dotnet restore src/CodeMemory.AspNet.Extensions/CodeMemory.AspNet.Extensions.csproj
RUN dotnet publish src/CodeMemory.AspNet.Extensions/CodeMemory.AspNet.Extensions.csproj -c Release -r linux-x64 --no-self-contained -o /extensions

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y git libgomp1 && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /data/cloned-repos && chown app:app /data/cloned-repos
USER app
WORKDIR /app
COPY --from=build /app .
COPY --from=build /extensions /app/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CodeMemory.AspNet.dll"]