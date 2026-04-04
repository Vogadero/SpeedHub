# SpeedHub Docker Image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 38457 38458 443 80 22 9418

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SpeedHub.Core/SpeedHub.Core.csproj ./SpeedHub.Core/
COPY src/SpeedHub.Web/SpeedHub.Web.csproj ./SpeedHub.Web/
RUN dotnet restore "SpeedHub.Web/SpeedHub.Web.csproj"
COPY . .
RUN dotnet publish "src/SpeedHub.Web/SpeedHub.Web.csproj" -c Release -o /app/publish

# Runtime stage
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# 复制配置文件
COPY appsettings*.json ./
COPY appsettings ./appsettings/

# 创建证书目录
RUN mkdir -p cacert

ENTRYPOINT ["dotnet", "SpeedHub.Web.dll"]
