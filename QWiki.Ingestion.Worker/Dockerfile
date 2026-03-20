FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY QWiki.Ingestion.Worker/QWiki.Ingestion.Worker.csproj QWiki.Ingestion.Worker/
COPY QWiki.Ingestion/QWiki.Ingestion.csproj QWiki.Ingestion/
COPY QWiki.Shared/QWiki.Shared.csproj QWiki.Shared/
RUN dotnet restore QWiki.Ingestion.Worker/QWiki.Ingestion.Worker.csproj
COPY QWiki.Ingestion.Worker/ QWiki.Ingestion.Worker/
COPY QWiki.Ingestion/ QWiki.Ingestion/
COPY QWiki.Shared/ QWiki.Shared/
RUN dotnet publish QWiki.Ingestion.Worker/QWiki.Ingestion.Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "QWiki.Ingestion.Worker.dll"]
