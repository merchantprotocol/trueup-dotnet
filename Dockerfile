# Test image: builds the SDK and runs its tests (live tests need TRUEUP_API_KEY).
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /sdk
COPY . .
RUN dotnet restore tests/TrueUp.Tests/TrueUp.Tests.csproj && dotnet build -c Release src/TrueUp/TrueUp.csproj -f net8.0 --no-restore
CMD ["dotnet", "test", "tests/TrueUp.Tests/TrueUp.Tests.csproj", "--logger", "console;verbosity=normal"]
