@echo off
setlocal enabledelayedexpansion

echo Compiling shaders...

for %%f in (Shaders\*.frag Shaders\*.vert Shaders\*.comp) do (
    echo Compiling %%f...
    glslc "%%f" -o "%%f.spv"
    if !errorlevel! neq 0 (
        echo Failed to compile %%f
        pause
        exit /b !errorlevel!
    )
)

echo Shaders compiled successfully.
pause