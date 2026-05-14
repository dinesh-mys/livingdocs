# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/LivingDocs.Core/LivingDocs.Core.csproj           src/LivingDocs.Core/
COPY src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj src/LivingDocs.CopilotExt/
RUN dotnet restore src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj

COPY src/LivingDocs.Core/       src/LivingDocs.Core/
COPY src/LivingDocs.CopilotExt/ src/LivingDocs.CopilotExt/

RUN dotnet publish src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj \
    -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user for least-privilege execution
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "LivingDocs.CopilotExt.dll"]
