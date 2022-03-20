@echo off
if exist ".usingproj" (
    echo "Switching to package references"
    dnt switch-to-packages
    del ".usingproj"
) else (
    echo "Switching to local project references"
    dnt switch-to-projects
    copy NUL ".usingproj"
)