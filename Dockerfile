FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/FaqBot.Api/FaqBot.Api.csproj
RUN dotnet publish src/FaqBot.Api/FaqBot.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /publish .
COPY data/ ./data/
RUN mkdir -p /app/appdata && chown -R "$APP_UID":0 /app/appdata
ENV ASPNETCORE_URLS=http://+:5108
EXPOSE 5108
USER $APP_UID
ENTRYPOINT ["dotnet", "FaqBot.Api.dll"]
