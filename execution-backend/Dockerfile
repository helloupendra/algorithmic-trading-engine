# --- STAGE 1: Build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project file first to cache the NuGet package restore
COPY ["ExecutionEngine.csproj", "./"]
RUN dotnet restore "ExecutionEngine.csproj"

# Copy the rest of the code and compile it in Release mode
COPY . .
RUN dotnet publish "ExecutionEngine.csproj" -c Release -o /app/publish /p:UseAppHost=false

# --- STAGE 2: Run ---
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Copy only the compiled binaries from the build stage
COPY --from=build /app/publish .

# Start the background worker
ENTRYPOINT ["dotnet", "ExecutionEngine.dll"]