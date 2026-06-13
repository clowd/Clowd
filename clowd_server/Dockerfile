# build:  docker build -t clowd-server clowd_server
# run:    docker run -p 8080:8080 \
#           -v clowd-cache:/data/cache \
#           -v clowd-redirects:/data/redirects \
#           clowd-server

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Clowd.Server/Clowd.Server.csproj Clowd.Server/
RUN dotnet restore Clowd.Server/Clowd.Server.csproj
COPY Clowd.Server/ Clowd.Server/
RUN dotnet publish Clowd.Server/Clowd.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# transient buffer for in-progress uploads; safe to wipe between runs
VOLUME ["/data/cache"]
# persisted id -> destination-url redirects; must survive restarts
VOLUME ["/data/redirects"]

ENV ASPNETCORE_URLS=http://+:8080 \
    Clowd__CachePath=/data/cache \
    Clowd__RedirectsPath=/data/redirects

EXPOSE 8080
ENTRYPOINT ["dotnet", "Clowd.Server.dll"]
