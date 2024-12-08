#!/usr/bin/env sh
dotnet publish -c Release -r osx-arm64
LIB_NAME=libUltraSampler.dylib
LIB_OUTPUT_PATH=./bin/Release/net8.0/osx-arm64/publish/$LIB_NAME
#install_name_tool -id @loader/$LIB_NAME $LIB_OUTPUT_PATH
install_name_tool -id @loader_path/$LIB_NAME $LIB_OUTPUT_PATH
cp $LIB_OUTPUT_PATH ./$LIB_NAME
clang -dynamiclib -O2 -o libUltraSamplerIndirect.dyld ultra_sampler_indirect.cpp -L . -l UltraSampler
#install_name_tool -change $LIB_NAME @loader/$LIB_NAME libUltraSamplerIndirect.dyld
