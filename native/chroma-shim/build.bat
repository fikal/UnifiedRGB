@echo off
setlocal
REM Build the UnifiedRGB Chroma shim: the 64-bit RzChromaSDK64.dll (what
REM Wallpaper Engine and 64-bit games load) AND the 32-bit RzChromaSDK.dll
REM (32-bit Chroma games; both live in Razer Chroma SDK\bin). Each build runs in
REM its own environment block so the two vcvarsall calls don't stack.
REM
REM Run this BEFORE building the solution. UnifiedRgb.Core embeds both DLLs as
REM EmbeddedResource behind a Condition="Exists(...)", so a solution built while
REM they are missing succeeds quietly and ships an app with no Chroma support at
REM all. CI has a step that fails when they did not make it into the assembly.
cd /d "%~dp0"

REM The toolset is located rather than hardcoded. This used to name VS18
REM Professional at a literal path, which built here and nowhere else: not on a
REM machine with a different edition or year, and not on the CI runner, which is
REM why CI had to repeat the compile lines instead of just calling this script.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VCVARSALL="
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo *** vswhere.exe not found - is Visual Studio or are the Build Tools installed? ***
    exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARSALL=%%i\VC\Auxiliary\Build\vcvarsall.bat"
if not defined VCVARSALL (
    echo *** no MSVC C++ toolset found - install the "Desktop development with C++" workload ***
    exit /b 1
)
if not exist "%VCVARSALL%" (
    echo *** vcvarsall.bat missing at "%VCVARSALL%" ***
    exit /b 1
)

setlocal
call "%VCVARSALL%" x64
if errorlevel 1 exit /b 1
rc /nologo /fo version.res version.rc
if errorlevel 1 exit /b 1
cl /nologo /LD /MT /EHsc /O2 /std:c++17 RzChromaSDK.cpp version.res /Fo:RzChromaSDK64.obj /link /DEF:RzChromaSDK.def /IMPLIB:RzChromaSDK64.lib /OUT:RzChromaSDK64.dll
if errorlevel 1 echo *** x64 BUILD FAILED - any RzChromaSDK64.dll below is STALE ***
if errorlevel 1 exit /b 1
echo.
echo === x64 exports ===
dumpbin /nologo /exports RzChromaSDK64.dll > exports-x64.txt
if errorlevel 1 exit /b 1
type exports-x64.txt
endlocal

setlocal
REM x64_x86 needs the x86 TARGETING libraries, a separate install component from
REM the x64 toolset vswhere was asked for above. When this fails that is almost
REM always what is missing, rather than anything about this script.
call "%VCVARSALL%" x64_x86
if errorlevel 1 (
    echo *** could not enter the x86 build environment ***
    echo *** install the "MSVC ... x64/x86 build tools" component in the Visual Studio Installer ***
    exit /b 1
)
rc /nologo /D SHIM32 /fo version32.res version.rc
if errorlevel 1 exit /b 1
cl /nologo /LD /MT /EHsc /O2 /std:c++17 RzChromaSDK.cpp version32.res /Fo:RzChromaSDK32.obj /link /DEF:RzChromaSDK32.def /IMPLIB:RzChromaSDK32.lib /OUT:RzChromaSDK.dll
if errorlevel 1 echo *** x86 BUILD FAILED - any RzChromaSDK.dll below is STALE ***
if errorlevel 1 exit /b 1
echo.
echo === x86 exports ===
dumpbin /nologo /exports RzChromaSDK.dll > exports-x86.txt
if errorlevel 1 exit /b 1
type exports-x86.txt
endlocal
endlocal
exit /b 0
