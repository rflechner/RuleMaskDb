FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/RuleMaskDb/RuleMaskDb/ src/RuleMaskDb/RuleMaskDb/
COPY src/RuleMaskDb/RuleMaskDb.ConsoleApp/ src/RuleMaskDb/RuleMaskDb.ConsoleApp/
COPY tests/integration/Runner/ tests/integration/Runner/
RUN dotnet publish src/RuleMaskDb/RuleMaskDb.ConsoleApp -c Release -o /out/cli
RUN dotnet publish tests/integration/Runner -c Release -o /out/runner

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Npgsql loads GSSAPI even when the fixture uses password authentication.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/cli /app/cli
COPY --from=build /out/runner /app/runner
COPY tests/integration/examples /app/examples
ENTRYPOINT ["dotnet", "/app/runner/RuleMaskDb.E2E.dll"]
