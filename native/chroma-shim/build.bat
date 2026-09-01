@echo off
REM Build the UnifiedRGB Chroma shim (RzChromaSDK64.dll) with VS18.
setlocal
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvarsall.bat" x64
cd /d "%~dp0"
rc /nologo /fo version.res version.rc
cl /nologo /LD /EHsc /O2 /std:c++17 RzChromaSDK.cpp version.res /link /DEF:RzChromaSDK.def /OUT:RzChromaSDK64.dll
echo.
echo === exports ===
dumpbin /exports RzChromaSDK64.dll | findstr /R "Init CreateKeyboardEffect CreateChromaLinkEffect SetEffect"
endlocal
