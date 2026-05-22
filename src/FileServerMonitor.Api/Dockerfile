FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG ENABLE_SQLSERVER=true

COPY src/FileServerMonitor.Api/FileServerMonitor.Api.csproj src/FileServerMonitor.Api/
COPY src/FileServerMonitor.Core/FileServerMonitor.Core.csproj src/FileServerMonitor.Core/
RUN dotnet restore src/FileServerMonitor.Api/FileServerMonitor.Api.csproj /p:EnableSqlServer=$ENABLE_SQLSERVER

COPY src/FileServerMonitor.Api/ src/FileServerMonitor.Api/
COPY src/FileServerMonitor.Core/ src/FileServerMonitor.Core/
RUN dotnet publish src/FileServerMonitor.Api/FileServerMonitor.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:EnableSqlServer=$ENABLE_SQLSERVER

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FileServerMonitor.Api.dll"]
