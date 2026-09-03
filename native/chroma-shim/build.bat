@echo off
REM Build the UnifiedRGB Chroma shim with VS18: the 64-bit RzChromaSDK64.dll
REM (what Wallpaper Engine and 64-bit games load) AND the 32-bit RzChromaSDK.dll
REM (32-bit Chroma games; both live in Razer Chroma SDK\bin). Each build runs in
REM its own environment block so the two vcvarsall calls don't stack.
cd /d "%~dp0"

setlocal
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvarsall.bat" x64
rc /nologo /fo version.res version.rc
cl /nologo /LD /MT /EHsc /O2 /std:c++17 RzChromaSDK.cpp version.res /Fo:RzChromaSDK64.obj /link /DEF:RzChromaSDK.def /IMPLIB:RzChromaSDK64.lib /OUT:RzChromaSDK64.dll
if errorlevel 1 echo *** x64 BUILD FAILED - any RzChromaSDK64.dll below is STALE ***
if errorlevel 1 exit /b 1
echo.
echo === x64 exports ===
dumpbin /exports RzChromaSDK64.dll | findstr /R "Init CreateKeyboardEffect CreateChromaLinkEffect SetEffect"
endlocal

setlocal
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvarsall.bat" x64_x86
rc /nologo /D SHIM32 /fo version32.res version.rc
cl /nologo /LD /MT /EHsc /O2 /std:c++17 RzChromaSDK.cpp version32.res /Fo:RzChromaSDK32.obj /link /DEF:RzChromaSDK32.def /IMPLIB:RzChromaSDK32.lib /OUT:RzChromaSDK.dll
if errorlevel 1 echo *** x86 BUILD FAILED - any RzChromaSDK.dll below is STALE ***
if errorlevel 1 exit /b 1
echo.
echo === x86 exports ===
dumpbin /exports RzChromaSDK.dll | findstr /R "Init CreateKeyboardEffect CreateChromaLinkEffect SetEffect"
endlocal
