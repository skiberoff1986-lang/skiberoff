FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/CraneJournal.Shared/CraneJournal.Shared.csproj src/CraneJournal.Shared/
COPY src/CraneJournal.Web/CraneJournal.Web.csproj src/CraneJournal.Web/
COPY checks/CraneJournal.Checks.csproj checks/
RUN dotnet restore checks/CraneJournal.Checks.csproj
COPY src/ src/
COPY checks/ checks/
RUN dotnet build checks/CraneJournal.Checks.csproj -c Release --no-restore
RUN dotnet run --project checks/CraneJournal.Checks.csproj -c Release --no-build -- --unit
RUN dotnet publish src/CraneJournal.Web/CraneJournal.Web.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM build AS checks
ENTRYPOINT ["dotnet", "/source/checks/bin/Release/net10.0/CraneJournal.Checks.dll"]
CMD ["--integration"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV PORT=8080
ENV DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CraneJournal.Web.dll"]
