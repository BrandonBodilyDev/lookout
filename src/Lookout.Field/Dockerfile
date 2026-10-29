# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Lookout.Engine/Lookout.Engine.csproj src/Lookout.Engine/
COPY src/Lookout.Link/Lookout.Link.csproj src/Lookout.Link/
COPY src/Lookout.Drone/Lookout.Drone.csproj src/Lookout.Drone/
COPY src/Lookout.Field/Lookout.Field.csproj src/Lookout.Field/
RUN dotnet restore src/Lookout.Field/Lookout.Field.csproj
COPY src/ src/
RUN dotnet publish src/Lookout.Field/Lookout.Field.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
COPY scenarios/ scenarios/
ENV SCENARIO_DIR=/app/scenarios
USER app
ENTRYPOINT ["dotnet", "Lookout.Field.dll"]
