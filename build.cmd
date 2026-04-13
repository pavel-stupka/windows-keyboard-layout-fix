@echo off
rem ============================================================================
rem build.cmd - KbFix build entry point
rem Stages: argparse -> sdk -> clean -> build -> test -> report
rem See specs\002-build-script\contracts\cli.md for the full contract.
rem ============================================================================
setlocal EnableExtensions EnableDelayedExpansion

rem --- repository root and well-known paths (T003) -----------------------------
set "REPO_ROOT=%~dp0"
if "%REPO_ROOT:~-1%"=="\" set "REPO_ROOT=%REPO_ROOT:~0,-1%"
set "SOLUTION_PATH=%REPO_ROOT%\KbFix.sln"
set "PROJECT_PATH=%REPO_ROOT%\src\KbFix\KbFix.csproj"
set "DEFAULT_OUTPUT_DIR=%REPO_ROOT%\dist"

rem --- defaults (T006) ---------------------------------------------------------
set "CONFIGURATION=Debug"
set "CONFIG_LABEL=Debug"
set "OUTPUT_DIR=%DEFAULT_OUTPUT_DIR%"
set "DO_CLEAN=1"
set "DO_TEST=0"
set "TEST_LABEL=skipped"
set "HELP_ONLY=0"
set "POSITIONAL_SEEN=0"

rem --- parse arguments (T013, T014, T018, T020-T023) ---------------------------
:parse_args
if "%~1"=="" goto parse_done

rem help flags (T018)
if /i "%~1"=="--help" ( set "HELP_ONLY=1" & shift & goto parse_args )
if /i "%~1"=="-h"     ( set "HELP_ONLY=1" & shift & goto parse_args )
if "%~1"=="/?"        ( set "HELP_ONLY=1" & shift & goto parse_args )

rem flag-only switches
if /i "%~1"=="--no-clean" ( set "DO_CLEAN=0" & shift & goto parse_args )
if /i "%~1"=="--test" ( set "DO_TEST=1" & set "TEST_LABEL=run" & shift & goto parse_args )

rem --output <path>
if /i "%~1"=="--output" (
    if "%~2"=="" (
        call :print_usage
        call :fail argparse "--output requires a path"
        exit /b !ERRORLEVEL!
    )
    call :resolve_output "%~2"
    if errorlevel 1 exit /b !ERRORLEVEL!
    shift
    shift
    goto parse_args
)

rem --output=<path>
set "ARG=%~1"
if /i "!ARG:~0,9!"=="--output=" (
    set "OUTPUT_VALUE=!ARG:~9!"
    if "!OUTPUT_VALUE!"=="" (
        call :print_usage
        call :fail argparse "--output requires a path"
        exit /b !ERRORLEVEL!
    )
    call :resolve_output "!OUTPUT_VALUE!"
    if errorlevel 1 exit /b !ERRORLEVEL!
    shift
    goto parse_args
)

rem any other token starting with - or -- is an unknown switch (T023)
if "!ARG:~0,1!"=="-" (
    call :print_usage
    call :fail argparse "unknown option '%~1'"
    exit /b !ERRORLEVEL!
)

rem positional configuration token (T013)
if "%POSITIONAL_SEEN%"=="1" (
    call :print_usage
    call :fail argparse "only one configuration token is allowed"
    exit /b !ERRORLEVEL!
)
if /i "%~1"=="debug" (
    set "CONFIGURATION=Debug"
    set "CONFIG_LABEL=Debug"
    set "POSITIONAL_SEEN=1"
    shift
    goto parse_args
)
if /i "%~1"=="release" (
    set "CONFIGURATION=Release"
    set "CONFIG_LABEL=Release"
    set "POSITIONAL_SEEN=1"
    shift
    goto parse_args
)

rem unrecognised positional token (T014 / T019)
call :print_usage
call :fail argparse "unknown configuration '%~1'. Accepted: debug, release"
exit /b !ERRORLEVEL!

:parse_done

rem help short-circuit (T018)
if "%HELP_ONLY%"=="1" (
    call :print_usage
    exit /b 0
)

rem --- verify we are inside a KbFix checkout (T003) ----------------------------
if not exist "%SOLUTION_PATH%" (
    call :fail argparse "not inside a KbFix checkout (missing %SOLUTION_PATH%)"
    exit /b !ERRORLEVEL!
)
if not exist "%PROJECT_PATH%" (
    call :fail argparse "not inside a KbFix checkout (missing %PROJECT_PATH%)"
    exit /b !ERRORLEVEL!
)

rem --- SDK check (T004) --------------------------------------------------------
call :check_sdk
if errorlevel 1 exit /b !ERRORLEVEL!

rem --- pre-build banner (T007 / T024) ------------------------------------------
echo [build.cmd] building !CONFIG_LABEL! -^> !OUTPUT_DIR! (tests: !TEST_LABEL!)

rem --- dispatch (T010 / T015 / T021) -------------------------------------------
call :stage_clean
if errorlevel 1 exit /b !ERRORLEVEL!

call :stage_build
if errorlevel 1 exit /b !ERRORLEVEL!

if "%DO_TEST%"=="1" (
    call :stage_test
    if errorlevel 1 exit /b !ERRORLEVEL!
)

goto :report_ok


rem =============================================================================
rem Subroutines
rem =============================================================================

:check_sdk
rem T004 - verify dotnet CLI is available and at least one SDK is installed.
dotnet --list-sdks >nul 2>&1
if errorlevel 1 (
    >&2 echo [build.cmd] FAILED at sdk: .NET 8 SDK not found on PATH. Install from https://dot.net
    exit /b 2
)
exit /b 0

:resolve_output
rem T022 - resolve a user-supplied --output path against REPO_ROOT, reject
rem paths that would cause the clean step to nuke source code.
set "USER_OUTPUT=%~1"
for %%I in ("%USER_OUTPUT%") do set "RESOLVED=%%~fI"
rem If the user passed a bare relative path, %%~fI resolves it against the
rem caller's cwd. We want it resolved against REPO_ROOT instead.
echo %USER_OUTPUT% | findstr /r /c:"^[A-Za-z]:" /c:"^\\\\" /c:"^\\" /c:"^/" >nul
if errorlevel 1 (
    rem relative path -> anchor to REPO_ROOT
    for %%I in ("%REPO_ROOT%\%USER_OUTPUT%") do set "RESOLVED=%%~fI"
)
rem strip trailing backslash for comparison
if "!RESOLVED:~-1!"=="\" set "RESOLVED=!RESOLVED:~0,-1!"

rem forbidden paths (V-OUTPUT-NOT-REPO)
if /i "!RESOLVED!"=="%REPO_ROOT%" (
    call :print_usage
    call :fail argparse "--output must not resolve to the repository root (!RESOLVED!)"
    exit /b !ERRORLEVEL!
)
if /i "!RESOLVED!"=="%REPO_ROOT%\src" (
    call :print_usage
    call :fail argparse "--output must not resolve to src\ (!RESOLVED!)"
    exit /b !ERRORLEVEL!
)
if /i "!RESOLVED!"=="%REPO_ROOT%\tests" (
    call :print_usage
    call :fail argparse "--output must not resolve to tests\ (!RESOLVED!)"
    exit /b !ERRORLEVEL!
)
if /i "!RESOLVED!"=="%REPO_ROOT%\.git" (
    call :print_usage
    call :fail argparse "--output must not resolve to .git\ (!RESOLVED!)"
    exit /b !ERRORLEVEL!
)

set "OUTPUT_DIR=!RESOLVED!"
exit /b 0

:stage_clean
rem T008 / T020 - clean or preserve the output directory.
if "%DO_CLEAN%"=="0" (
    echo [build.cmd] skipping clean (--no-clean^)
    if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%" >nul 2>&1
    exit /b 0
)
if exist "%OUTPUT_DIR%" (
    rmdir /s /q "%OUTPUT_DIR%"
    if errorlevel 1 (
        call :fail clean "unable to clear %OUTPUT_DIR% (is a file locked?)"
        exit /b !ERRORLEVEL!
    )
)
mkdir "%OUTPUT_DIR%"
if errorlevel 1 (
    call :fail clean "unable to create %OUTPUT_DIR%"
    exit /b !ERRORLEVEL!
)
exit /b 0

:stage_build
rem T009 - publish the KbFix project in the selected configuration.
dotnet publish "%PROJECT_PATH%" -c %CONFIGURATION% -o "%OUTPUT_DIR%" --nologo
if errorlevel 1 (
    set "BUILD_EXIT=!ERRORLEVEL!"
    call :fail build "dotnet publish exited with code !BUILD_EXIT!"
    exit /b !BUILD_EXIT!
)
exit /b 0

:stage_test
rem T021 - run the solution's test suite in the same configuration.
dotnet test "%SOLUTION_PATH%" -c %CONFIGURATION% --nologo
if errorlevel 1 (
    set "TEST_EXIT=!ERRORLEVEL!"
    call :fail test "dotnet test exited with code !TEST_EXIT!"
    exit /b !TEST_EXIT!
)
exit /b 0

:report_ok
rem T005 - success banner.
echo [build.cmd] OK: !CONFIG_LABEL! build written to !OUTPUT_DIR! (tests: !TEST_LABEL!)
exit /b 0

:fail
rem T005 - unified failure banner. %~1 = stage, %~2 = reason.
>&2 echo [build.cmd] FAILED at %~1: %~2
exit /b 1

:print_usage
rem T017 - stable usage block.
echo Usage: build.cmd [debug^|release] [options]
echo.
echo Build the KbFix utility into a disposable output directory.
echo.
echo Configurations:
echo   debug     Debug build (default when no configuration is given^)
echo   release   Release build
echo.
echo Options:
echo   --help, -h, /?       Print this message and exit.
echo   --test               Run the test suite after a successful build.
echo   --output ^<path^>      Write artifacts to ^<path^> instead of the default.
echo                        Relative paths are resolved against the repo root.
echo   --no-clean           Skip the pre-build clean of the output directory.
echo.
echo Default configuration: debug
echo Default output:        %DEFAULT_OUTPUT_DIR%
echo.
echo Examples:
echo   build.cmd
echo   build.cmd release
echo   build.cmd release --test
echo   build.cmd debug --output out\local
echo   build.cmd release --no-clean
exit /b 0
