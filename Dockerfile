FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install --no-install-recommends -y clang zlib1g-dev && apt-get clean
WORKDIR /src
ARG GITHUB_ACTIONS
ARG GITHUB_REF
ARG GITHUB_SHA
ENV NBGV_GitEngine=Disabled \
    GITHUB_ACTIONS=${GITHUB_ACTIONS} \
    GITHUB_REF=${GITHUB_REF} \
    GITHUB_SHA=${GITHUB_SHA}
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY version.json ./
COPY src/MaddyBot/ src/MaddyBot/
COPY src/idunno.Bluesky.BotSdk/ src/idunno.Bluesky.BotSdk/
RUN dotnet restore src/MaddyBot/MaddyBot.csproj
RUN dotnet publish src/MaddyBot/MaddyBot.csproj --configuration Release --runtime linux-x64 --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
USER root
RUN apt-get update && apt-get install --no-install-recommends -y curl && apt-get clean
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=2s --start-period=60s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["./MaddyBot"]
