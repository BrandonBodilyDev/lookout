# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Lookout.Engine/Lookout.Engine.csproj src/Lookout.Engine/
COPY src/Lookout.Link/Lookout.Link.csproj src/Lookout.Link/
COPY src/Lookout.Api/Lookout.Api.csproj src/Lookout.Api/
RUN dotnet restore src/Lookout.Api/Lookout.Api.csproj
COPY src/ src/
RUN dotnet publish src/Lookout.Api/Lookout.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
COPY scenarios/ scenarios/
ENV ASPNETCORE_URLS=http://+:8080 \
    Lookout__ScenarioDir=/app/scenarios
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Lookout.Api.dll"]
