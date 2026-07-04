#!/usr/bin/env sh
# Build script for the compiling the C# sampler to native
dotnet publish -c Release -r osx-arm64
LIB_NAME=libUltraSampler.dylib
LIB_OUTPUT_PATH=./bin/Release/net8.0/osx-arm64/publish/$LIB_NAME
#install_name_tool -id @loader/$LIB_NAME $LIB_OUTPUT_PATH
install_name_tool -id @loader_path/$LIB_NAME $LIB_OUTPUT_PATH
cp $LIB_OUTPUT_PATH ./$LIB_NAME
clang -dynamiclib -O2 -o libUltraSamplerHook.dylib ultra_sampler_hook.cpp -L . -l UltraSampler
