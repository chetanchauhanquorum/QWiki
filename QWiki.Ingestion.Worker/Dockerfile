FROM mcr.microsoft.com/dotnet/runtime:9.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY . .
ENTRYPOINT ["dotnet", "QWiki.Ingestion.Worker.dll"]
