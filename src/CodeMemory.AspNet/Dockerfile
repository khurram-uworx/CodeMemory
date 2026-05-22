FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/CodeMemory.AspNet/CodeMemory.AspNet.csproj
RUN dotnet publish src/CodeMemory.AspNet/CodeMemory.AspNet.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /data/cloned-repos && chown app:app /data/cloned-repos
USER app
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CodeMemory.AspNet.dll"]