FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang libclang-rt-dev curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .

RUN dotnet publish -c Release -o /app libbgcode.NET.Fuzz/libbgcode.NET.Fuzz.csproj

# Seed with the uninstrumented build; instrumented assemblies only run
# under the fuzzer (the SharpFuzz trace memory is not set up otherwise).
RUN dotnet /src/libbgcode.NET.Fuzz/bin/Release/net10.0/libbgcode.NET.Fuzz.dll seed /seed-corpus

RUN dotnet tool install --tool-path /tools SharpFuzz.CommandLine \
 && /tools/sharpfuzz /app/libbgcode.NET.dll \
 && /tools/sharpfuzz /app/MeatPack.NET.dll

RUN curl -sfL -o /tmp/libfuzzer-dotnet.cc \
        https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/master/libfuzzer-dotnet.cc \
 && clang++ -fsanitize=fuzzer -O2 -std=c++17 -o /usr/local/bin/libfuzzer-dotnet /tmp/libfuzzer-dotnet.cc

ENTRYPOINT ["/src/libbgcode.NET.Fuzz/container-entry.sh"]
