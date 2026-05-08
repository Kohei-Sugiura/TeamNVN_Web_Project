FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Weepa.Api.csproj ./
RUN dotnet restore Weepa.Api.csproj

COPY . ./
RUN dotnet publish Weepa.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./

# Chromium + 日本語フォント (Discord 用ランキング PNG レンダリングで Puppeteer から使う)
RUN apt-get update && apt-get install -y --no-install-recommends \
        chromium \
        fonts-noto-cjk \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Weepa.Api.dll"]
