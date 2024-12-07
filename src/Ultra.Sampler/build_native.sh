#!/usr/bin/env sh
dotnet publish -c Release -r osx-arm64
LIB_NAME=libUltraSampler.dylib
LIB_OUTPUT_PATH=./bin/Release/net8.0/osx-arm64/publish/$LIB_NAME
install_name_tool -id $LIB_NAME $LIB_OUTPUT_PATH
cp $LIB_OUTPUT_PATH ./$LIB_NAME
clang -shared -O2 -o libUltraSamplerIndirect.dyld ultra_sampler_indirect.cpp ./$LIB_NAME
