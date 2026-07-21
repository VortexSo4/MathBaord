@echo off
echo Compiling shaders...

glslc Shaders\stroke.frag -o Shaders\stroke.frag.spv
if %errorlevel% neq 0 (
    echo Failed to compile stroke.frag
    pause
    exit /b %errorlevel%
)

glslc Shaders\stroke.vert -o Shaders\stroke.vert.spv
if %errorlevel% neq 0 (
    echo Failed to compile stroke.vert
    pause
    exit /b %errorlevel%
)

glslc Shaders\text.frag -o Shaders\text.frag.spv
if %errorlevel% neq 0 (
    echo Failed to compile text.frag
    pause
    exit /b %errorlevel%
)

glslc Shaders\text.vert -o Shaders\text.vert.spv
if %errorlevel% neq 0 (
    echo Failed to compile text.vert
    pause
    exit /b %errorlevel%
)

echo Shaders compiled successfully.
pause